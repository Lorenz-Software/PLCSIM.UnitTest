using TwinCAT.Ads;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed record SystemStateRead(AdsState State, int RestartCounter);

internal sealed record SystemObservation(Guid Context, long Sequence, DateTimeOffset ObservedAt,
    SystemStateRead? Value, string? Error)
{
    internal bool IsRun => Value?.State == AdsState.Run && Error is null;
}

// Runtime observation owns publication. Consumers wait without holding the ADS IO gate.
internal sealed class SystemObservationMonitor(TimeProvider timeProvider)
{
    private readonly object gate = new();
    private SystemObservation latest = new(Guid.NewGuid(), 0, timeProvider.GetUtcNow(), null, "Not observed.");
    private TaskCompletionSource changed = NewSignal();
    private bool stopped;

    internal SystemObservation Latest { get { lock (gate) return latest; } }

    internal bool TryPublish(long expectedSequence, SystemStateRead value)
    {
        lock (gate)
        {
            if (stopped || latest.Sequence != expectedSequence) return false;
            Publish(value, null);
            return true;
        }
    }

    internal void Invalidate(string error)
    {
        lock (gate)
        {
            if (!stopped) Publish(null, error);
        }
    }

    private void Publish(SystemStateRead? value, string? error)
    {
        latest = new(latest.Context, latest.Sequence + 1, timeProvider.GetUtcNow(), value, error);
        var signal = changed;
        changed = NewSignal();
        signal.TrySetResult();
    }

    internal async Task<SystemObservation> WaitForRunAsync(SystemObservation baseline, bool requireRestart,
        TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (requireRestart && baseline.Value is null)
            throw new InvalidOperationException("A restart wait requires an observed system baseline.");
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Task next;
                lock (gate)
                {
                    if (stopped) throw new IOException("Runtime observation context has been released.");
                    if (baseline.Context != latest.Context)
                        throw new ArgumentException("The observation belongs to a different Runtime context.", nameof(baseline));
                    if (latest.Sequence > baseline.Sequence && latest.IsRun && (!requireRestart
                        || latest.Value!.RestartCounter != 0 && latest.Value.RestartCounter != baseline.Value!.RestartCounter))
                        return latest;
                    next = changed.Task;
                }
                await next.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("No fresh confirmed TwinCAT system RUN observation satisfied the readiness condition.");
        }
    }

    internal void Stop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            Publish(null, "Runtime observation context has been released.");
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
