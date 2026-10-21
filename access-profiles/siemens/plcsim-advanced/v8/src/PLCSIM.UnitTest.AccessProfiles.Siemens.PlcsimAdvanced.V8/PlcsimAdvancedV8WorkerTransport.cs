using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8;

internal sealed class PlcsimAdvancedV8WorkerTransport : IDisposable
{
    private static readonly TimeSpan WorkerCommandCompletionGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerCloseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WorkerTerminationTimeout = TimeSpan.FromSeconds(15);

    private readonly Process process;
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim commandLock = new(1, 1);
    private readonly BlockingCollection<string> commandResponses = new();
    private readonly CancellationTokenSource readerCancellation = new();
    private readonly Task readerTask;
    private readonly TimeSpan commandResponseTimeout;
    private readonly TimeSpan workerCloseTimeout;
    private readonly Func<string, bool> tryHandleAsyncMessage;
    private readonly Action<string> publishConnectionLost;
    private string? activeRequestId;
    private int disposed;
    private int closeRequested;

    public PlcsimAdvancedV8WorkerTransport(
        Process process,
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer,
        TimeSpan commandResponseTimeout,
        Func<string, bool> tryHandleAsyncMessage,
        Action<string> publishConnectionLost,
        TimeSpan? workerCloseTimeout = null)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        this.pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.commandResponseTimeout = commandResponseTimeout;
        this.workerCloseTimeout = workerCloseTimeout ?? WorkerCloseTimeout;
        if (this.workerCloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCloseTimeout));
        }

        this.tryHandleAsyncMessage = tryHandleAsyncMessage ?? throw new ArgumentNullException(nameof(tryHandleAsyncMessage));
        this.publishConnectionLost = publishConnectionLost ?? throw new ArgumentNullException(nameof(publishConnectionLost));
        readerTask = Task.Run(ReadWorkerMessagesAsync);
    }

    public int WorkerProcessId => process.Id;

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public bool IsAvailable => !IsDisposed && !process.HasExited && pipe.IsConnected;

    public Task AcquireCommandAsync(CancellationToken cancellationToken)
    {
        return commandLock.WaitAsync(cancellationToken);
    }

    public void ReleaseCommand()
    {
        commandLock.Release();
    }

    public void ThrowIfCancellationRequestedBeforeDispatch(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        commandLock.Release();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public string CreateCommandRequest(
        string type,
        params KeyValuePair<string, string>[] values)
    {
        var requestId = Guid.NewGuid().ToString("N");
        activeRequestId = requestId;
        return PlcsimAdvancedV8WorkerProtocol.CreateRequest(
            type,
            new[]
            {
                new KeyValuePair<string, string>("requestId", requestId)
            }.Concat(values).ToArray());
    }

    public async Task SendRequestAsync(
        string request,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(request).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> ReadCommandResponseLineAsync(
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null)
    {
        var now = DateTimeOffset.UtcNow;
        var responseTimeout = ResolveCommandResponseTimeout(
            commandResponseTimeout,
            operationTimeout);
        var maximumTimeout = DateTimeOffset.MaxValue - now;
        var deadline = now.Add(responseTimeout > maximumTimeout ? maximumTimeout : responseTimeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (commandResponses.IsCompleted)
            {
                return Task.FromResult<string?>(null);
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            var wait = remaining < TimeSpan.FromMilliseconds(100)
                ? remaining
                : TimeSpan.FromMilliseconds(100);
            var waitMilliseconds = Math.Max(1, (int)Math.Ceiling(wait.TotalMilliseconds));
            if (commandResponses.TryTake(out var line, waitMilliseconds, cancellationToken))
            {
                var message = PlcsimAdvancedV8WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return Task.FromResult<string?>(line);
                }

                var expectedRequestId = activeRequestId;
                if (!string.IsNullOrWhiteSpace(expectedRequestId)
                    && message.Values.TryGetValue("requestId", out var responseRequestId)
                    && string.Equals(expectedRequestId, responseRequestId, StringComparison.Ordinal))
                {
                    return Task.FromResult<string?>(line);
                }

                Dispose();
                return Task.FromResult<string?>(
                    PlcsimAdvancedV8WorkerProtocol.CreateRequest(
                        "Error",
                        new KeyValuePair<string, string>("requestId", expectedRequestId ?? string.Empty),
                        new KeyValuePair<string, string>("status", "Failed"),
                        new KeyValuePair<string, string>(
                            "code",
                            "PlcsimAdvanced.V8.Worker.ResponseCorrelationMismatch"),
                        new KeyValuePair<string, string>(
                            "message",
                            "PLCSIM Advanced V8 worker response did not match the active IPC request.")));
            }
        }
    }

    internal static TimeSpan ResolveCommandResponseTimeout(
        TimeSpan commandResponseTimeout,
        TimeSpan? operationTimeout)
    {
        if (operationTimeout is null || operationTimeout <= TimeSpan.Zero)
        {
            return commandResponseTimeout;
        }

        var maximumOperationTimeout = TimeSpan.MaxValue - WorkerCommandCompletionGrace;
        var boundedOperationTimeout = operationTimeout > maximumOperationTimeout
            ? maximumOperationTimeout
            : operationTimeout.Value;
        var operationResponseTimeout = boundedOperationTimeout + WorkerCommandCompletionGrace;
        return operationResponseTimeout > commandResponseTimeout
            ? operationResponseTimeout
            : commandResponseTimeout;
    }

    public void Terminate(string message)
    {
        publishConnectionLost(message);
        Dispose();
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return;
        }

        if (process.HasExited)
        {
            Dispose();
            return;
        }

        using var closeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closeCancellation.CancelAfter(workerCloseTimeout);
        var commandLockAcquired = false;
        try
        {
            await commandLock.WaitAsync(closeCancellation.Token).ConfigureAwait(false);
            commandLockAcquired = true;
            Interlocked.Exchange(ref closeRequested, 1);
            await SendRequestAsync(CreateCommandRequest("Close"), closeCancellation.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(closeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "PLCSIM Advanced V8 worker did not exit within the close timeout.",
                ex);
        }
        finally
        {
            Dispose();
            if (commandLockAcquired)
            {
                commandLock.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            readerCancellation.Cancel();
        }
        catch
        {
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit((int)WorkerTerminationTimeout.TotalMilliseconds);
            }
        }
        catch
        {
        }

        try
        {
            writer.Dispose();
        }
        catch
        {
        }

        try
        {
            reader.Dispose();
        }
        catch
        {
        }

        try
        {
            pipe.Dispose();
        }
        catch
        {
        }

        try
        {
            process.Dispose();
        }
        catch
        {
        }

        try
        {
            readerCancellation.Dispose();
            commandResponses.Dispose();
        }
        catch
        {
        }
    }

    private async Task ReadWorkerMessagesAsync()
    {
        string? disconnectMessage = null;
        try
        {
            while (!readerCancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(readerCancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    disconnectMessage = "PLCSIM Advanced V8 worker closed the named-pipe connection.";
                    break;
                }

                if (tryHandleAsyncMessage(line))
                {
                    continue;
                }

                commandResponses.Add(line, readerCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException ex)
        {
            disconnectMessage = ex.Message;
        }
        catch (IOException ex)
        {
            disconnectMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            disconnectMessage = ex.Message;
        }
        finally
        {
            try
            {
                commandResponses.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            if (Volatile.Read(ref closeRequested) == 0
                && !readerCancellation.IsCancellationRequested)
            {
                publishConnectionLost(
                    string.IsNullOrWhiteSpace(disconnectMessage)
                        ? "PLCSIM Advanced V8 worker pipe reader ended unexpectedly."
                        : disconnectMessage);
                Dispose();
            }
        }
    }
}
