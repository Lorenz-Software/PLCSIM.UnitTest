namespace PLCSIM.UnitTest.Core;

internal sealed class CoreOperationCancellationScope : IDisposable
{
    private readonly CancellationToken callerCancellationToken;
    private readonly CancellationTokenSource deadlineCancellation = new();
    private readonly CancellationTokenSource linkedCancellation;
    private readonly ITimer deadlineTimer;

    public CoreOperationCancellationScope(
        TimeProvider timeProvider,
        TimeSpan timeout,
        CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Operation timeout must be greater than zero.");
        }

        Timeout = timeout;
        this.callerCancellationToken = callerCancellationToken;
        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken,
            deadlineCancellation.Token);
        deadlineTimer = timeProvider.CreateTimer(
            static state => CancelDeadline((CancellationTokenSource)state!),
            deadlineCancellation,
            timeout,
            System.Threading.Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Timeout { get; }

    public CancellationToken Token => linkedCancellation.Token;

    public bool CallerCancellationRequested => callerCancellationToken.IsCancellationRequested;

    public bool DeadlineExpired => deadlineCancellation.IsCancellationRequested;

    public void Dispose()
    {
        deadlineTimer.Dispose();
        linkedCancellation.Dispose();
        deadlineCancellation.Dispose();
    }

    private static void CancelDeadline(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
