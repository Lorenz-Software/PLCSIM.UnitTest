namespace PLCSIM.UnitTest.App.Cli;

public sealed class CliCancellationController : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private int requestCount;

    public CancellationToken Token => cancellation.Token;

    public bool RequestCancellation()
    {
        if (Interlocked.Increment(ref requestCount) != 1)
        {
            return false;
        }

        try
        {
            cancellation.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException)
        {
            // Cancellation callbacks must not prevent the second Ctrl+C fallback.
        }

        return true;
    }

    public void Dispose()
    {
        cancellation.Dispose();
    }
}
