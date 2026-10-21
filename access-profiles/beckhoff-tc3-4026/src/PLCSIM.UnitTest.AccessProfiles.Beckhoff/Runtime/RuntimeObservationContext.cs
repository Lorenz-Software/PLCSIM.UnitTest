using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Services;
using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record PreparedRuntimeTarget(LocalUsermodeRuntime Local, int? PlcPort, RuntimeProvisioningMode Mode,
    RuntimeProvisioningStrategy Strategy, PreparedPlcRuntime Result)
{
    internal int? ConfiguredPlcPort { get; init; }
}

internal sealed class RuntimeObservationContext(PreparedRuntimeTarget descriptor, IAdsRuntimeAccess access, TimeProvider? timeProvider = null)
{
    private readonly object signalGate = new();
    private bool stopped;
    private long invalidationGeneration;
    private long dataBindingGeneration;
    private bool dataUnavailable;
    private long verifiedGeneration = -1;
    private DateTimeOffset nextEarlyCheck;
    public PreparedRuntimeTarget Descriptor { get; set; } = descriptor;
    public bool PlcObservationSuspended { get; set; }
    public IAdsRuntimeAccess Access { get; } = access;
    public SystemObservationMonitor SystemMonitor { get; } = new(timeProvider ?? TimeProvider.System);
    public CancellationTokenSource Lifetime { get; } = new();
    public SemaphoreSlim Wake { get; } = new(0, 1);
    public SemaphoreSlim Io { get; } = new(1, 1);
    public Task Work { get; set; } = Task.CompletedTask;
    public AdsState? LastSystem { get; set; }
    public AdsState? LastPlc { get; set; }
    public bool PlcPortMissing { get; set; }
    public bool SystemObserved { get; set; }
    public bool IdentityVerified { get; set; } = true;
    public bool Quarantined { get; set; }
    public string? Problem { get; set; }
    public string ProblemCode { get; set; } = "ObservationFailed";
    public string? PublishedProblem { get; set; }
    public bool ConfigReported { get; set; }
    public int PlcReadRequested;

    public long InvalidationGeneration { get { lock (signalGate) return invalidationGeneration; } }
    public long DataBindingGeneration { get { lock (signalGate) return dataBindingGeneration; } }

    public void RecordDataAvailability(bool available)
    {
        lock (signalGate)
        {
            if (stopped) return;
            if (!available && !dataUnavailable) dataBindingGeneration++;
            dataUnavailable = !available;
        }
    }
    public bool HasVerifiedDataAccess
    {
        get
        {
            lock (signalGate)
                return !stopped && verifiedGeneration == invalidationGeneration && SystemMonitor.Latest.IsRun;
        }
    }

    public void ConfirmPlcObservation(long generation)
    {
        lock (signalGate)
        {
            // A read started before a newer loss cannot restore data access.
            if (!stopped && generation == invalidationGeneration) verifiedGeneration = generation;
        }
    }

    public void OnSystemStateInvalidated(object? sender, EventArgs args)
    {
        lock (signalGate)
        {
            if (stopped) return;
            invalidationGeneration++;
            dataBindingGeneration++;
            SystemMonitor.Invalidate("System session connection was lost.");
            Signal();
        }
    }

    public void OnPlcStateChanged(object? sender, EventArgs args)
    {
        lock (signalGate)
        {
            if (stopped) return;
            Signal();
        }
    }

    public void OnPlcStateInvalidated(object? sender, EventArgs args)
    {
        lock (signalGate)
        {
            if (stopped) return;
            invalidationGeneration++;
            dataBindingGeneration++;
            Signal();
        }
    }

    public void RequestEarlyCheck(DateTimeOffset now, TimeSpan interval)
    {
        lock (signalGate)
        {
            if (stopped || now < nextEarlyCheck) return;
            nextEarlyCheck = now + interval;
            Signal();
        }
    }

    private void Signal()
    {
        Interlocked.Exchange(ref PlcReadRequested, 1);
        if (Wake.CurrentCount == 0) Wake.Release();
    }

    public void Stop()
    {
        lock (signalGate) stopped = true;
        SystemMonitor.Stop();
        Lifetime.Cancel();
    }

    public void DisposeSynchronization()
    {
        lock (signalGate) stopped = true;
        Lifetime.Dispose();
        Wake.Dispose();
        Io.Dispose();
    }
}
