namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

internal sealed class CancellableOperationController : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellationSource;
    private TaskCompletionSource? completion;
    private string? operationName;
    private bool cancellationRequested;

    public event EventHandler? StateChanged;

    public bool IsActive
    {
        get
        {
            lock (sync)
            {
                return cancellationSource is not null;
            }
        }
    }

    public bool IsCancellationRequested
    {
        get
        {
            lock (sync)
            {
                return cancellationRequested;
            }
        }
    }

    public string? OperationName
    {
        get
        {
            lock (sync)
            {
                return operationName;
            }
        }
    }

    public async Task RunAsync(
        string name,
        Func<CancellationToken, Task> operation,
        Func<Task> onCanceled,
        CancellationToken callerCancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onCanceled);

        using var source = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        var operationCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (sync)
        {
            if (cancellationSource is not null)
            {
                throw new InvalidOperationException(
                    $"Cancellable operation '{operationName}' is already active.");
            }

            cancellationSource = source;
            completion = operationCompletion;
            operationName = name;
            cancellationRequested = false;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await operation(source.Token);
            if (source.IsCancellationRequested)
            {
                await onCanceled();
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            await onCanceled();
        }
        finally
        {
            lock (sync)
            {
                cancellationSource = null;
                completion = null;
                operationName = null;
                cancellationRequested = false;
            }

            operationCompletion.TrySetResult();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool RequestCancellation()
    {
        CancellationTokenSource? source;
        lock (sync)
        {
            if (cancellationSource is null || cancellationRequested)
            {
                return false;
            }

            cancellationRequested = true;
            source = cancellationSource;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        source.Cancel();
        return true;
    }

    public async Task CancelAndWaitAsync()
    {
        Task? pendingCompletion;
        lock (sync)
        {
            pendingCompletion = completion?.Task;
        }

        RequestCancellation();
        if (pendingCompletion is not null)
        {
            await pendingCompletion;
        }
    }

    public void Dispose()
    {
        RequestCancellation();
    }
}
