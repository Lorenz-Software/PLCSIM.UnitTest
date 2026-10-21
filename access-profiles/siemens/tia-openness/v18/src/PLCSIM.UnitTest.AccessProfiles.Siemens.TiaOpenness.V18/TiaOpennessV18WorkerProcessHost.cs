using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18;

internal sealed class TiaOpennessV18WorkerProcessHost
{
    internal const string WorkerExecutableName =
        "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18.Worker.exe";

    private static readonly TimeSpan WorkerPipeConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkerInitialResponseTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan WorkerExitGracePeriod = TimeSpan.FromSeconds(2);

    public async Task<TiaOpennessV18WorkerProcessStartResult> StartAsync(
        ProcessStartInfo startInfo,
        string pipeName,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        TiaOpennessV18BoundedProcessErrorCapture? errorCapture = null;
        NamedPipeClientStream? pipe = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return TiaOpennessV18WorkerProcessStartResult.Failed(
                    "TiaOpenness.V18.EngineeringSessionWorkerStartFailed",
                    "TIA Openness V18 engineering-session worker process could not be started.",
                    Array.Empty<TiaEvidence>());
            }

            errorCapture = TiaOpennessV18BoundedProcessErrorCapture.Start(process);
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
                return TiaOpennessV18WorkerProcessStartResult.Failed(
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
                        ? "TIA Openness V18 engineering-session worker exited before reporting a session result."
                        : "TIA Openness V18 engineering-session worker exited before reporting a session result: " + error.Trim()
                    : "TIA Openness V18 engineering-session worker did not report a session result before the timeout. Check for a pending TIA Openness firewall prompt and verify the deployed worker executable AllowList approval.";
                DisposePipe(reader, writer, pipe);
                DisposeProcess(process, errorCapture);
                return TiaOpennessV18WorkerProcessStartResult.Failed(
                    workerExited
                        ? "TiaOpenness.V18.EngineeringSessionWorkerNoResponse"
                        : "TiaOpenness.V18.EngineeringSessionOpenTimedOut",
                    message,
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v18-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
            }

            return TiaOpennessV18WorkerProcessStartResult.SucceededResult(
                new TiaOpennessV18WorkerProcessLifetime(
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
        return "PLCSIM.UnitTest.TiaOpenness.V18." + Guid.NewGuid().ToString("N");
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
        var assemblyLocation = typeof(TiaOpennessV18Binding).Assembly.Location;
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

    private static async Task<TiaOpennessV18PipeConnectResult> TryConnectPipeAsync(
        NamedPipeClientStream pipe,
        Process process,
        TiaOpennessV18BoundedProcessErrorCapture errorCapture,
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
                return TiaOpennessV18PipeConnectResult.Failed(
                    "TiaOpenness.V18.EngineeringSessionWorkerNoResponse",
                    "TIA Openness V18 engineering-session worker exited before opening the IPC pipe.",
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v18-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return TiaOpennessV18PipeConnectResult.Failed(
                    "TiaOpenness.V18.EngineeringSessionWorkerConnectTimedOut",
                    "TIA Openness V18 engineering-session worker did not open the IPC pipe before the connect timeout.",
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

        return TiaOpennessV18PipeConnectResult.SucceededResult();
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
        TiaOpennessV18BoundedProcessErrorCapture? errorCapture)
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

internal sealed class TiaOpennessV18WorkerProcessLifetime
{
    private static readonly TimeSpan WorkerTerminationTimeout = TimeSpan.FromSeconds(15);

    private readonly TiaOpennessV18BoundedProcessErrorCapture errorCapture;
    private TiaOpennessV18ArchiveWorkspace? archiveWorkspace;
    private int isTerminated;

    public TiaOpennessV18WorkerProcessLifetime(
        Process process,
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer,
        TiaOpennessV18BoundedProcessErrorCapture errorCapture)
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

    public void AdoptArchiveWorkspace(TiaOpennessV18ArchiveWorkspace? workspace)
    {
        if (IsTerminated)
        {
            throw new InvalidOperationException(
                "A terminated TIA Openness V18 worker cannot adopt an archive workspace.");
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
            await TiaOpennessV18WorkerProcessHost.WaitForProcessExitAsync(
                Process,
                TiaOpennessV18WorkerProcessHost.WorkerExitGracePeriod,
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

internal sealed record TiaOpennessV18WorkerProcessStartResult(
    bool Succeeded,
    TiaOpennessV18WorkerProcessLifetime? Lifetime,
    string? InitialResponse,
    string Code,
    string Message,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public static TiaOpennessV18WorkerProcessStartResult SucceededResult(
        TiaOpennessV18WorkerProcessLifetime lifetime,
        string initialResponse)
    {
        return new TiaOpennessV18WorkerProcessStartResult(
            true,
            lifetime,
            initialResponse,
            string.Empty,
            string.Empty,
            Array.Empty<TiaEvidence>());
    }

    public static TiaOpennessV18WorkerProcessStartResult Failed(
        string code,
        string message,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV18WorkerProcessStartResult(
            false,
            null,
            null,
            code,
            message,
            evidence);
    }
}

internal sealed record TiaOpennessV18PipeConnectResult(
    bool Succeeded,
    string Code,
    string Message,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public static TiaOpennessV18PipeConnectResult SucceededResult()
    {
        return new TiaOpennessV18PipeConnectResult(
            true,
            string.Empty,
            string.Empty,
            Array.Empty<TiaEvidence>());
    }

    public static TiaOpennessV18PipeConnectResult Failed(
        string code,
        string message,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV18PipeConnectResult(false, code, message, evidence);
    }
}
