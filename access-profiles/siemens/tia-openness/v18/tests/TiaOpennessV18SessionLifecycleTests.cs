using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V18;

public sealed class TiaOpennessV18SessionLifecycleTests
{
    [Fact]
    public async Task V18_engineering_session_marks_disposed_even_when_worker_close_throws()
    {
        var handle = new RecordingV18EngineeringSessionHandle(
            "tia-session-1",
            closeException: new IOException("close failed"));
        var session = new TiaOpennessV18EngineeringSession(handle);
        var health = new List<TiaProjectHealthChangedEventArgs>();
        session.HealthChanged += (_, args) => health.Add(args);

        await Assert.ThrowsAsync<IOException>(() => session.CloseAsync(CancellationToken.None));
        await session.CloseAsync(CancellationToken.None);

        Assert.Equal(TiaProjectHealthState.Disposed, session.Health);
        Assert.Equal(1, handle.CloseCount);
        Assert.Contains(health, update => update.Current == TiaProjectHealthState.Disposed);
    }
    [Fact]
    public async Task V18_closed_engineering_session_releases_event_subscribers()
    {
        var result = await CreateClosedSessionWithSubscriberAsync();

        ForceFullCollection();

        GC.KeepAlive(result.Session);
        Assert.False(result.Subscriber.IsAlive);
    }

    [Fact]
    public async Task V18_closed_engineering_session_and_handle_are_collectable()
    {
        var references = await CreateClosedSessionWeakReferencesAsync();

        ForceFullCollection();

        Assert.False(references.Session.IsAlive);
        Assert.False(references.Handle.IsAlive);
    }

    [Fact]
    public async Task V18_worker_close_timeout_includes_waiting_for_active_command_and_terminates_owned_worker()
    {
        var pipeName = "plcsim-unittest-tia-close-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var connect = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connect;

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "127.0.0.1 -n 30",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("The worker test process could not be started.");
        var processId = process.Id;
        var reader = new StreamReader(client);
        var writer = new StreamWriter(client) { AutoFlush = true };
        var lifetime = new TiaOpennessV18WorkerProcessLifetime(
            process,
            client,
            reader,
            writer,
            TiaOpennessV18BoundedProcessErrorCapture.Start(process));
        var dispatcher = new TiaOpennessV18WorkerCommandDispatcher(
            lifetime,
            TimeSpan.FromMilliseconds(100));

        var activeCommand = dispatcher.DispatchAsync(
            "held command",
            TiaOpennessV18WorkerProtocol.CreateRequest("Held"),
            _ => TiaOpennessV18WorkerCommandStep<bool>.Continue(),
            CancellationToken.None);
        using var serverReader = new StreamReader(server);
        _ = await serverReader.ReadLineAsync();

        var error = await Assert.ThrowsAsync<TiaOpennessV18WorkerSessionException>(
            () => dispatcher.CloseAsync(CancellationToken.None));

        Assert.Contains("close timeout", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(WaitForProcessExit(processId));
        await Assert.ThrowsAnyAsync<Exception>(() => activeCommand);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClosedSessionSubscriberResult> CreateClosedSessionWithSubscriberAsync()
    {
        var session = new TiaOpennessV18EngineeringSession(
            new RecordingV18EngineeringSessionHandle("subscriber-lifetime-session"));
        var subscriber = new SessionEventSubscriber();
        session.HealthChanged += subscriber.OnHealthChanged;
        session.ProgressChanged += subscriber.OnProgressChanged;
        var weakReference = new WeakReference(subscriber);

        await session.CloseAsync(CancellationToken.None);
        return new ClosedSessionSubscriberResult(session, weakReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<SessionWeakReferences> CreateClosedSessionWeakReferencesAsync()
    {
        var handle = new RecordingV18EngineeringSessionHandle("collectable-session");
        var session = new TiaOpennessV18EngineeringSession(handle);
        var references = new SessionWeakReferences(
            new WeakReference(session),
            new WeakReference(handle));

        await session.CloseAsync(CancellationToken.None);
        return references;
    }

    private static bool WaitForProcessExit(int processId)
    {
        return SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return process.HasExited;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            },
            TimeSpan.FromSeconds(2));
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private sealed record ClosedSessionSubscriberResult(
        TiaOpennessV18EngineeringSession Session,
        WeakReference Subscriber);

    private sealed record SessionWeakReferences(
        WeakReference Session,
        WeakReference Handle);

    private sealed class SessionEventSubscriber
    {
        public void OnHealthChanged(object? sender, TiaProjectHealthChangedEventArgs args)
        {
        }

        public void OnProgressChanged(object? sender, TiaProgressSnapshot snapshot)
        {
        }
    }
}
