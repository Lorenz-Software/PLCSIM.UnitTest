namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;

internal sealed class TiaOpennessV17WorkerCommandDispatcher
{
    private static readonly TimeSpan WorkerCloseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WorkerPostDispatchContainmentTimeout = TimeSpan.FromMinutes(10);

    private readonly TiaOpennessV17WorkerProcessLifetime processLifetime;
    private readonly TimeSpan workerCloseTimeout;
    private readonly SemaphoreSlim commandLock = new(1, 1);

    public TiaOpennessV17WorkerCommandDispatcher(
        TiaOpennessV17WorkerProcessLifetime processLifetime,
        TimeSpan? workerCloseTimeout = null)
    {
        this.processLifetime = processLifetime
            ?? throw new ArgumentNullException(nameof(processLifetime));
        this.workerCloseTimeout = workerCloseTimeout ?? WorkerCloseTimeout;
        if (this.workerCloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCloseTimeout));
        }
    }

    public async Task<TResult> DispatchAsync<TResult>(
        string operation,
        string request,
        Func<TiaOpennessV17WorkerProtocolMessage, TiaOpennessV17WorkerCommandStep<TResult>> handleMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var requestSent = false;
        try
        {
            ThrowIfUnavailable(operation);
            cancellationToken.ThrowIfCancellationRequested();

            using var commandCancellation = new CancellationTokenSource(WorkerPostDispatchContainmentTimeout);
            await processLifetime.Writer.WriteLineAsync(request).ConfigureAwait(false);
            requestSent = true;
            await processLifetime.Writer.FlushAsync(commandCancellation.Token).ConfigureAwait(false);

            while (true)
            {
                var line = await processLifetime.Reader.ReadLineAsync(commandCancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw CreateTransportException(
                        $"TIA Openness V17 worker disconnected during {operation}.");
                }

                var message = TiaOpennessV17WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    throw CreateTransportException(
                        $"TIA Openness V17 worker returned an unsupported protocol response during {operation}.");
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase)
                    && IsSessionInvalidated(message))
                {
                    _ = TiaOpennessV17WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    throw CreateTransportException(
                        TiaOpennessV17WorkerProtocol.ReadRequiredField(
                            message.Values,
                            "message",
                            "Error"));
                }

                var step = handleMessage(message);
                if (!step.IsTerminal)
                {
                    continue;
                }

                ThrowIfCallerCanceledAfterDispatch(cancellationToken, operation);
                return step.Result!;
            }
        }
        catch (TiaOpennessV17WorkerCommandCanceledException)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (requestSent)
        {
            processLifetime.Terminate();
            throw CreateTransportException(
                $"TIA Openness V17 {operation} did not reach a terminal worker response before the post-dispatch containment timeout.",
                ex);
        }
        catch (IOException ex)
        {
            processLifetime.Terminate();
            throw CreateTransportException(
                $"TIA Openness V17 worker communication failed during {operation}.",
                ex);
        }
        catch (TiaOpennessV17WorkerSessionException)
        {
            processLifetime.Terminate();
            throw;
        }
        finally
        {
            commandLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (processLifetime.IsTerminated)
        {
            return;
        }

        using var closeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closeCancellation.CancelAfter(workerCloseTimeout);
        var commandLockAcquired = false;
        try
        {
            await commandLock.WaitAsync(closeCancellation.Token).ConfigureAwait(false);
            commandLockAcquired = true;
            if (processLifetime.IsTerminated)
            {
                return;
            }

            await processLifetime.Writer.WriteLineAsync(TiaOpennessV17WorkerProtocol.CreateRequest("Close")).ConfigureAwait(false);
            await processLifetime.Writer.FlushAsync(closeCancellation.Token).ConfigureAwait(false);

            var line = await processLifetime.Reader.ReadLineAsync(closeCancellation.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw CreateTransportException(
                    "TIA Openness V17 worker disconnected before reporting its cleanup result.");
            }

            var message = TiaOpennessV17WorkerProtocol.ParseMessage(line);
            if (!message.IsProtocolVersionSupported
                || !string.Equals(message.Type, "CloseResult", StringComparison.OrdinalIgnoreCase))
            {
                throw CreateTransportException(
                    "TIA Openness V17 worker returned an unexpected cleanup response.");
            }

            var status = TiaOpennessV17WorkerProtocol.ReadRequiredOperationStatus(
                message.Values,
                "status",
                "CloseResult");
            if (status != PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.TiaOperationStatus.Succeeded)
            {
                var workerMessage = TiaOpennessV17WorkerProtocol.ReadRequiredField(
                    message.Values,
                    "message",
                    "CloseResult");
                throw CreateTransportException(
                    "TIA Openness V17 worker cleanup failed: " + workerMessage);
            }

            await processLifetime.WaitForExitAsync(closeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTransportException(
                "TIA Openness V17 worker cleanup did not complete before the close timeout.",
                ex);
        }
        finally
        {
            processLifetime.Terminate();
            if (commandLockAcquired)
            {
                commandLock.Release();
            }
        }
    }

    private void ThrowIfUnavailable(string operation)
    {
        if (!processLifetime.IsAvailable)
        {
            throw CreateTransportException(
                $"TIA Openness V17 worker is not available for {operation}.");
        }
    }

    private static bool IsSessionInvalidated(TiaOpennessV17WorkerProtocolMessage message)
    {
        return message.Values.TryGetValue("sessionState", out var state)
            && string.Equals(state, "Invalidated", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfCallerCanceledAfterDispatch(
        CancellationToken cancellationToken,
        string operation)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        throw new TiaOpennessV17WorkerCommandCanceledException(
            $"TIA Openness V17 {operation} was canceled after dispatch. The terminal worker response was drained and the engineering session remains available; cancellation did not roll back vendor-side effects.",
            new OperationCanceledException(cancellationToken),
            cancellationToken);
    }

    internal TiaOpennessV17WorkerSessionException CreateTransportException(
        string message,
        Exception? innerException = null)
    {
        var workerError = processLifetime.CompleteErrorCapture();
        var detail = string.IsNullOrWhiteSpace(workerError)
            ? message
            : message + " Worker stderr: " + workerError;
        return new TiaOpennessV17WorkerSessionException(detail, innerException);
    }
}

internal sealed record TiaOpennessV17WorkerCommandStep<TResult>(
    bool IsTerminal,
    TResult? Result)
{
    public static TiaOpennessV17WorkerCommandStep<TResult> Continue()
    {
        return new TiaOpennessV17WorkerCommandStep<TResult>(false, default);
    }

    public static TiaOpennessV17WorkerCommandStep<TResult> Complete(TResult result)
    {
        return new TiaOpennessV17WorkerCommandStep<TResult>(true, result);
    }
}
