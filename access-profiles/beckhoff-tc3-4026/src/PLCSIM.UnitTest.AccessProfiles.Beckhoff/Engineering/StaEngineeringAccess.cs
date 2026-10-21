using System.Collections.Concurrent;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed class StaEngineeringAccess : IEngineeringProjectAccess
{
    private readonly Func<IXaeProjectAccess> createAccess;
    private readonly BlockingCollection<(Action Run, Action<Exception> Fail)> work = new();
    private readonly TaskCompletionSource terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private IXaeProjectAccess? access;
    private Task? close;
    private CancellationToken currentCancellation;

    internal StaEngineeringAccess(Func<IXaeProjectAccess>? createAccess = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("TwinCAT engineering requires Windows.");
        this.createAccess = createAccess ?? (() => new XaeProjectAccess());
        var thread = new Thread(Run) { Name = "Beckhoff XAE STA", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task OpenAsync(ProjectWorkspace workspace, CancellationToken cancellationToken) =>
        Invoke(() => { (access ??= createAccess()).Open(workspace, cancellationToken); return true; }, cancellationToken);

    public Task<IReadOnlyList<EngineeringPlcTarget>> DiscoverAsync(CancellationToken cancellationToken) =>
        Invoke(() => (access ?? throw new InvalidOperationException("XAE project is not open.")).Discover(cancellationToken), cancellationToken);

    public Task EnsureStandardPlcAsync(EngineeringPlcTarget target, CancellationToken cancellationToken) =>
        Invoke(() => { (access ?? throw new InvalidOperationException("XAE project is not open.")).EnsureStandardPlc(target, cancellationToken); return true; }, cancellationToken);

    public Task<EngineeringBuildResult> BuildAsync(EngineeringPlcTarget target, CancellationToken cancellationToken) =>
        Invoke(() => (access ?? throw new InvalidOperationException("XAE project is not open.")).Build(target, cancellationToken), cancellationToken);

    public Task ActivateAsync(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken) =>
        Invoke(() => { (access ?? throw new InvalidOperationException("XAE project is not open.")).Activate(target, netId, cancellationToken); return true; }, cancellationToken);

    public Task DownloadAsync(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken) =>
        Invoke(() => { (access ?? throw new InvalidOperationException("XAE project is not open.")).Download(target, netId, cancellationToken); return true; }, cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (close is not null && !close.IsFaulted && !close.IsCanceled) return close;
            cancellationToken.ThrowIfCancellationRequested();
            return close = CloseCoreAsync(cancellationToken);
        }
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        try { await initialized.Task.ConfigureAwait(false); }
        catch { await terminated.Task.ConfigureAwait(false); return; }
        // Closing must finish on the owner STA before the thread and archive can be released.
        await Enqueue(() => { access?.Close(); access = null; return true; }, cancellationToken).ConfigureAwait(false);
        work.CompleteAdding();
        await terminated.Task.ConfigureAwait(false);
    }

    private Task<T> Invoke<T>(Func<T> action, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (close is not null) throw new InvalidOperationException("Engineering access is closing; it cannot accept another operation.");
            return Enqueue(action, cancellationToken);
        }
    }

    private async Task<T> Enqueue<T>(Func<T> action, CancellationToken cancellationToken)
    {
        // Queue before initialization completes so an immediate Close cannot overtake Open.
        if (initialized.Task.IsFaulted) await initialized.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add((() =>
        {
            currentCancellation = cancellationToken;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(action());
            }
            catch (OperationCanceledException error) { completion.SetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.SetException(error); }
            finally { currentCancellation = default; }
        }, error => completion.TrySetException(error)));
        // A dispatched COM call still owns the context; do not abandon it with WaitAsync(token).
        return await completion.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        try
        {
            using (var apartment = new XaeComApartment(() => currentCancellation.IsCancellationRequested))
            {
                initialized.SetResult();
                while (!work.IsCompleted)
                {
                    if (work.TryTake(out var item, 20)) item.Run();
                    XaeComApartment.PumpMessages();
                }
            }
            terminated.TrySetResult();
        }
        catch (Exception error)
        {
            var initializationFailed = initialized.TrySetException(error);
            work.CompleteAdding();
            while (work.TryTake(out var item)) item.Fail(error);
            if (initializationFailed) terminated.TrySetResult();
            else terminated.TrySetException(error);
        }
    }
}
