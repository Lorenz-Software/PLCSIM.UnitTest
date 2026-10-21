namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

// One serialized reader; requests arriving during its read remain pending.
internal sealed class RunControlObservationSignal
{
    private readonly object gate = new();
    private TaskCompletionSource pending = NewSignal();
    private bool closed;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Request()
    {
        lock (gate) { if (!closed) pending.TrySetResult(); }
    }

    internal void BeginRead()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (pending.Task.IsCompleted) pending = NewSignal();
        }
    }

    internal async Task WaitAsync(TimeSpan fallback, TimeProvider clock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task work;
        lock (gate) work = pending.Task;
        try { await work.WaitAsync(fallback, clock, token).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    internal void Close()
    {
        lock (gate) { closed = true; pending.TrySetResult(); }
    }
}
