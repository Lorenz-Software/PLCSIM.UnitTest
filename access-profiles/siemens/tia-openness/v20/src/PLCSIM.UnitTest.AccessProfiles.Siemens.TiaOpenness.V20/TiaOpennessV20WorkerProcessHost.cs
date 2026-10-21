using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;

internal sealed class TiaOpennessV20WorkerProcessHost
{
    internal const string WorkerExecutableName =
        "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20.Worker.exe";

    private static readonly TimeSpan WorkerPipeConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkerInitialResponseTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan WorkerExitGracePeriod = TimeSpan.FromSeconds(2);

    public async Task<TiaOpennessV20WorkerProcessStartResult> StartAsync(
        ProcessStartInfo startInfo,
        string pipeName,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        TiaOpennessV20BoundedProcessErrorCapture? errorCapture = null;
        NamedPipeClientStream? pipe = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return TiaOpennessV20WorkerProcessStartResult.Failed(
                    "TiaOpenness.V20.EngineeringSessionWorkerStartFailed",
                    "TIA Openness V20 engineering-session worker process could not be started.",
                    Array.Empty<TiaEvidence>());
            }

            errorCapture = TiaOpennessV20BoundedProcessErrorCapture.Start(process);
            pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            var connect = await TryConnectPipeAsync(
                pipe,
                process,
                errorCapture,
                WorkerPipeConnectTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!connect.Succeeded)
            {
                DisposePipe(reader, writer, pipe);
                DisposeProcess(process, errorCapture);
                return TiaOpennessV20WorkerProcessStartResult.Failed(
                    connect.Code,
                    connect.Message,
                    connect.Evidence);
            }

            reader = new StreamReader(pipe, Encoding.UTF8, true, 1024, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };

            var line = await ReadInitialWorkerResponseAsync(
                reader,
                WorkerInitialResponseTimeout,
                cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                await WaitForProcessExitAsync(
                    process,
                    WorkerExitGracePeriod,
                    cancellationToken).ConfigureAwait(false);
                var error = errorCapture.CompleteAndSnapshot();
                var workerExited = process.HasExited;
                var message = workerExited
                    ? string.IsNullOrWhiteSpace(error)
                        ? "TIA Openness V20 engineering-session worker exited before reporting a session result."
                        : "TIA Openness V20 engineering-session worker exited before reporting a session result: " + error.Trim()
                    : "TIA Openness V20 engineering-session worker did not report a session result before the timeout. Check for a pending TIA Openness firewall prompt and verify the deployed worker executable AllowList approval.";
                DisposePipe(reader, writer, pipe);
                DisposeProcess(process, errorCapture);
                return TiaOpennessV20WorkerProcessStartResult.Failed(
                    workerExited
                        ? "TiaOpenness.V20.EngineeringSessionWorkerNoResponse"
                        : "TiaOpenness.V20.EngineeringSessionOpenTimedOut",
                    message,
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v20-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
            }

            return TiaOpennessV20WorkerProcessStartResult.SucceededResult(
                new TiaOpennessV20WorkerProcessLifetime(
                    process,
                    pipe,
                    reader,
                    writer,
                    errorCapture),
                line);
        }
        catch
        {
            DisposePipe(reader, writer, pipe);
            DisposeProcess(process, errorCapture);
            throw;
        }
    }

    internal static ProcessStartInfo CreateWorkerStartInfo(
        string workerPath,
        string pipeName,
        string publicApiDirectory,
        string projectSource,
        TiaProjectOpenMode openMode,
        string? archiveWorkDirectory,
        SiemensBindingContext bindingContext,
        string workerInstanceId)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddArgument(startInfo, "--pipe", pipeName);
        AddArgument(startInfo, "--public-api", publicApiDirectory);
        AddArgument(startInfo, "--project-source", projectSource);
        AddArgument(startInfo, "--project-open-mode", openMode.ToString());
        if (!string.IsNullOrWhiteSpace(archiveWorkDirectory))
        {
            AddArgument(startInfo, "--archive-work-directory", archiveWorkDirectory);
        }

        AddArgument(startInfo, "--access-profile-id", bindingContext.AccessProfileId);
        AddArgument(startInfo, "--worker-instance-id", workerInstanceId);
        AddArgument(startInfo, "--log-directory", bindingContext.Logging.LogDirectory);
        AddArgument(startInfo, "--log-level", bindingContext.Logging.MinimumLevel.ToString());
        AddArgument(
            startInfo,
            "--log-file-size-limit-bytes",
            bindingContext.Logging.FileSizeLimitBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(
            startInfo,
            "--log-retained-file-count",
            bindingContext.Logging.RetainedFileCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    internal static ProcessStartInfo CreateAuthorizationProbeStartInfo(
        string workerPath,
        string pipeName,
        string publicApiDirectory,
        SiemensBindingContext bindingContext,
        string workerInstanceId)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddArgument(startInfo, "--pipe", pipeName);
        AddArgument(startInfo, "--public-api", publicApiDirectory);
        AddArgument(startInfo, "--authorization-probe", "true");
        AddArgument(startInfo, "--access-profile-id", bindingContext.AccessProfileId);
        AddArgument(startInfo, "--worker-instance-id", workerInstanceId);
        AddArgument(startInfo, "--log-directory", bindingContext.Logging.LogDirectory);
        AddArgument(startInfo, "--log-level", bindingContext.Logging.MinimumLevel.ToString());
        AddArgument(
            startInfo,
            "--log-file-size-limit-bytes",
            bindingContext.Logging.FileSizeLimitBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(
            startInfo,
            "--log-retained-file-count",
            bindingContext.Logging.RetainedFileCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    internal static string CreatePipeName()
    {
        return "PLCSIM.UnitTest.TiaOpenness.V20." + Guid.NewGuid().ToString("N");
    }

    internal static string ResolveDeployedWorkerPath()
    {
        var bindingDirectory = ResolveBindingDirectory();
        return Path.Combine(bindingDirectory, WorkerExecutableName);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string ResolveBindingDirectory()
    {
        var assemblyLocation = typeof(TiaOpennessV20Binding).Assembly.Location;
        var assemblyDirectory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? null
            : Path.GetDirectoryName(assemblyLocation);

        return string.IsNullOrWhiteSpace(assemblyDirectory)
            ? AppContext.BaseDirectory
            : assemblyDirectory;
    }

    private static async Task<string?> ReadInitialWorkerResponseAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var readTask = reader.ReadLineAsync(cancellationToken).AsTask();
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
        if (completed == readTask)
        {
            return await readTask.ConfigureAwait(false);
        }

        return null;
    }

    internal static async Task WaitForProcessExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The process-disposal fallback handles workers that exceed the grace period.
        }
    }

    private static async Task<TiaOpennessV20PipeConnectResult> TryConnectPipeAsync(
        NamedPipeClientStream pipe,
        Process process,
        TiaOpennessV20BoundedProcessErrorCapture errorCapture,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!pipe.IsConnected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                var error = errorCapture.CompleteAndSnapshot();
                return TiaOpennessV20PipeConnectResult.Failed(
                    "TiaOpenness.V20.EngineeringSessionWorkerNoResponse",
                    "TIA Openness V20 engineering-session worker exited before opening the IPC pipe.",
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v20-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return TiaOpennessV20PipeConnectResult.Failed(
                    "TiaOpenness.V20.EngineeringSessionWorkerConnectTimedOut",
                    "TIA Openness V20 engineering-session worker did not open the IPC pipe before the connect timeout.",
                    Array.Empty<TiaEvidence>());
            }

            try
            {
                await pipe.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        return TiaOpennessV20PipeConnectResult.SucceededResult();
    }

    private static void DisposePipe(
        StreamReader? reader,
        StreamWriter? writer,
        NamedPipeClientStream? pipe)
    {
        try
        {
            reader?.Dispose();
        }
        catch
        {
        }

        try
        {
            writer?.Dispose();
        }
        catch
        {
        }

        try
        {
            pipe?.Dispose();
        }
        catch
        {
        }
    }

    private static void DisposeProcess(
        Process? process,
        TiaOpennessV20BoundedProcessErrorCapture? errorCapture)
    {
        errorCapture?.Dispose();
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)WorkerExitGracePeriod.TotalMilliseconds);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}

internal sealed class TiaOpennessV20WorkerProcessLifetime
{
    private static readonly TimeSpan WorkerTerminationTimeout = TimeSpan.FromSeconds(15);

    private readonly TiaOpennessV20BoundedProcessErrorCapture errorCapture;
    private TiaOpennessV20ArchiveWorkspace? archiveWorkspace;
    private int isTerminated;

    public TiaOpennessV20WorkerProcessLifetime(
        Process process,
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer,
        TiaOpennessV20BoundedProcessErrorCapture errorCapture)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        Pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        Reader = reader ?? throw new ArgumentNullException(nameof(reader));
        Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.errorCapture = errorCapture ?? throw new ArgumentNullException(nameof(errorCapture));
    }

    public Process Process { get; }

    public NamedPipeClientStream Pipe { get; }

    public StreamReader Reader { get; }

    public StreamWriter Writer { get; }

    public bool IsTerminated => Volatile.Read(ref isTerminated) != 0;

    public bool IsAvailable => !IsTerminated && !Process.HasExited && Pipe.IsConnected;

    public void AdoptArchiveWorkspace(TiaOpennessV20ArchiveWorkspace? workspace)
    {
        if (IsTerminated)
        {
            throw new InvalidOperationException(
                "A terminated TIA Openness V20 worker cannot adopt an archive workspace.");
        }

        archiveWorkspace = workspace;
    }

    public string CompleteErrorCapture()
    {
        return errorCapture.CompleteAndSnapshot();
    }

    public async Task<string> CompleteFailedStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A terminal error reply precedes worker disposal and asynchronous log flushing.
            await TiaOpennessV20WorkerProcessHost.WaitForProcessExitAsync(
                Process,
                TiaOpennessV20WorkerProcessHost.WorkerExitGracePeriod,
                cancellationToken).ConfigureAwait(false);
            return CompleteErrorCapture();
        }
        finally
        {
            Terminate();
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Terminate()
    {
        if (Interlocked.Exchange(ref isTerminated, 1) != 0)
        {
            return;
        }

        try
        {
            Reader.Dispose();
        }
        catch
        {
        }

        try
        {
            Writer.Dispose();
        }
        catch
        {
        }

        try
        {
            Pipe.Dispose();
        }
        catch
        {
        }

        errorCapture.Dispose();

        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit((int)WorkerTerminationTimeout.TotalMilliseconds);
            }
        }
        catch
        {
        }
        finally
        {
            Process.Dispose();
            archiveWorkspace?.TraceCleanupFailure();
        }
    }
}

internal sealed record TiaOpennessV20WorkerProcessStartResult(
    bool Succeeded,
    TiaOpennessV20WorkerProcessLifetime? Lifetime,
    string? InitialResponse,
    string Code,
    string Message,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public static TiaOpennessV20WorkerProcessStartResult SucceededResult(
        TiaOpennessV20WorkerProcessLifetime lifetime,
        string initialResponse)
    {
        return new TiaOpennessV20WorkerProcessStartResult(
            true,
            lifetime,
            initialResponse,
            string.Empty,
            string.Empty,
            Array.Empty<TiaEvidence>());
    }

    public static TiaOpennessV20WorkerProcessStartResult Failed(
        string code,
        string message,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV20WorkerProcessStartResult(
            false,
            null,
            null,
            code,
            message,
            evidence);
    }
}

internal sealed record TiaOpennessV20PipeConnectResult(
    bool Succeeded,
    string Code,
    string Message,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public static TiaOpennessV20PipeConnectResult SucceededResult()
    {
        return new TiaOpennessV20PipeConnectResult(
            true,
            string.Empty,
            string.Empty,
            Array.Empty<TiaEvidence>());
    }

    public static TiaOpennessV20PipeConnectResult Failed(
        string code,
        string message,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV20PipeConnectResult(false, code, message, evidence);
    }
}
