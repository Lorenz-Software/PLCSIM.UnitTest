using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;

internal sealed class PlcsimAdvancedV6WorkerBootstrap
{
    private const string WorkerExecutableName = "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker.exe";
    private static readonly TimeSpan WorkerPipeConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkerInitialResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string> workerPathResolver;

    internal PlcsimAdvancedV6WorkerBootstrap(Func<string> workerPathResolver)
    {
        this.workerPathResolver = workerPathResolver ?? throw new ArgumentNullException(nameof(workerPathResolver));
    }

    public async Task<PlcsimAdvancedV6WorkerBootstrapResult> OpenAsync(
        string runtimeApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(runtimeApiDirectory))
        {
            return PlcsimAdvancedV6WorkerBootstrapResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RuntimeApiDirectoryMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 worker requires a Runtime API directory.",
                    "OpenWorkerSession",
                    "plcsim-advanced-v6-worker-host"),
                Array.Empty<PlcsimEvidence>());
        }

        var workerPath = workerPathResolver();
        var pipeName = CreatePipeName();
        var workerInstanceId = Guid.NewGuid().ToString("N");
        if (!File.Exists(workerPath))
        {
            return PlcsimAdvancedV6WorkerBootstrapResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.WorkerMissing",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 worker executable was not found.",
                    "OpenWorkerSession",
                    "plcsim-advanced-v6-worker-host"),
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-worker-host",
                        "WorkerHost",
                        workerPath)
                });
        }

        var startInfo = CreateWorkerStartInfo(
            workerPath,
            pipeName,
            runtimeApiDirectory,
            bindingContext,
            workerInstanceId);

        Process? process = null;
        NamedPipeClientStream? pipe = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return PlcsimAdvancedV6WorkerBootstrapResult.Failed(
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.WorkerStartFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker process could not be started.",
                        "OpenWorkerSession",
                        "plcsim-advanced-v6-worker-host"),
                    Array.Empty<PlcsimEvidence>());
            }

            pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            var connect = await TryConnectPipeAsync(
                pipe,
                process,
                WorkerPipeConnectTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!connect.Succeeded)
            {
                DisposePipe(reader, writer, pipe);
                DisposeProcess(process);
                return PlcsimAdvancedV6WorkerBootstrapResult.Failed(
                    new PlcsimDiagnostic(
                        connect.Code,
                        PlcsimDiagnosticSeverity.Error,
                        connect.Message,
                        "OpenWorkerSession",
                        "plcsim-advanced-v6-worker-host"),
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
                var error = process.HasExited
                    ? await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
                    : string.Empty;
                DisposePipe(reader, writer, pipe);
                DisposeProcess(process);
                return PlcsimAdvancedV6WorkerBootstrapResult.Failed(
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.WorkerNoResponse",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker exited or timed out before reporting a session result.",
                        "OpenWorkerSession",
                        "plcsim-advanced-v6-worker-host"),
                    new[]
                    {
                        new PlcsimEvidence(
                            "plcsim-advanced-v6-worker-error",
                            "WorkerError",
                            error)
                    });
            }

            var response = PlcsimAdvancedV6WorkerResponse.Parse(line);
            if (!response.IsReady)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                DisposePipe(reader, writer, pipe);
                DisposeProcess(process);
                return PlcsimAdvancedV6WorkerBootstrapResult.Failed(
                    new PlcsimDiagnostic(
                        response.Code ?? "PlcsimAdvanced.V6.WorkerOpenFailed",
                        PlcsimDiagnosticSeverity.Error,
                        response.Message ?? "PLCSIM Advanced V6 worker failed to open a session.",
                        "OpenWorkerSession",
                        "plcsim-advanced-v6-worker-host"),
                    new[]
                    {
                        new PlcsimEvidence(
                            "plcsim-advanced-v6-worker-error",
                            "WorkerError",
                            error)
                    });
            }

            return PlcsimAdvancedV6WorkerBootstrapResult.SucceededResult(
                new PlcsimAdvancedV6WorkerConnection(
                    process,
                    pipe,
                    reader,
                    writer,
                    response.SessionId ?? process.Id.ToString()),
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-worker-host",
                        "WorkerHost",
                        workerPath),
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-worker-pipe",
                        "WorkerPipe",
                        pipeName),
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runtime-api-directory",
                        "RuntimeApiDirectory",
                        runtimeApiDirectory)
                });
        }
        catch (OperationCanceledException)
        {
            DisposePipe(reader, writer, pipe);
            DisposeProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            DisposePipe(reader, writer, pipe);
            DisposeProcess(process);
            return PlcsimAdvancedV6WorkerBootstrapResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.WorkerStartFailed",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 worker process could not be started.",
                    "OpenWorkerSession",
                    "plcsim-advanced-v6-worker-host"),
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-worker-error",
                        "WorkerError",
                        ex.Message)
                });
        }
    }

    internal static ProcessStartInfo CreateWorkerStartInfo(
        string workerPath,
        string pipeName,
        string runtimeApiDirectory,
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
        AddArgument(startInfo, "--runtime-api-directory", runtimeApiDirectory);

        AddArgument(startInfo, "--access-profile-id", bindingContext.AccessProfileId);
        AddArgument(startInfo, "--worker-instance-id", workerInstanceId);
        AddArgument(startInfo, "--log-directory", bindingContext.Logging.LogDirectory);
        AddArgument(startInfo, "--log-level", bindingContext.Logging.MinimumLevel.ToString());
        AddArgument(
            startInfo,
            "--log-file-size-limit-bytes",
            bindingContext.Logging.FileSizeLimitBytes.ToString(CultureInfo.InvariantCulture));
        AddArgument(
            startInfo,
            "--log-retained-file-count",
            bindingContext.Logging.RetainedFileCount.ToString(CultureInfo.InvariantCulture));
        AddArgument(
            startInfo,
            "--raw-event-logging-enabled",
            bindingContext.Logging.RawEventLoggingEnabled.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    internal static string ResolveWorkerPath()
    {
        var bindingDirectory = ResolveBindingDirectory();
        return Path.Combine(bindingDirectory, WorkerExecutableName);
    }

    private static async Task<string?> ReadInitialWorkerResponseAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var readTask = reader.ReadLineAsync(cancellationToken).AsTask();
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
        return completed == readTask
            ? await readTask.ConfigureAwait(false)
            : null;
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static async Task<PlcsimAdvancedV6PipeConnectResult> TryConnectPipeAsync(
        NamedPipeClientStream pipe,
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!pipe.IsConnected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return PlcsimAdvancedV6PipeConnectResult.Failed(
                    "PlcsimAdvanced.V6.WorkerNoResponse",
                    "PLCSIM Advanced V6 worker exited before opening the IPC pipe.",
                    new[]
                    {
                        new PlcsimEvidence(
                            "plcsim-advanced-v6-worker-error",
                            "WorkerError",
                            error)
                    });
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return PlcsimAdvancedV6PipeConnectResult.Failed(
                    "PlcsimAdvanced.V6.WorkerConnectTimedOut",
                    "PLCSIM Advanced V6 worker did not open the IPC pipe before the connect timeout.",
                    Array.Empty<PlcsimEvidence>());
            }

            try
            {
                pipe.Connect(250);
            }
            catch (TimeoutException)
            {
            }
        }

        return PlcsimAdvancedV6PipeConnectResult.SucceededResult();
    }

    private static string CreatePipeName()
    {
        return "PLCSIM.UnitTest.PlcsimAdvanced.V6." + Guid.NewGuid().ToString("N");
    }

    private static string ResolveBindingDirectory()
    {
        var assemblyLocation = typeof(PlcsimAdvancedV6Binding).Assembly.Location;
        var assemblyDirectory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? null
            : Path.GetDirectoryName(assemblyLocation);

        return string.IsNullOrWhiteSpace(assemblyDirectory)
            ? AppContext.BaseDirectory
            : assemblyDirectory;
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

    private static void DisposeProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
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

internal sealed record PlcsimAdvancedV6WorkerConnection(
    Process Process,
    NamedPipeClientStream Pipe,
    StreamReader Reader,
    StreamWriter Writer,
    string SessionId) : IDisposable
{
    public void Dispose()
    {
        try
        {
            Writer.Dispose();
        }
        catch
        {
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
            Pipe.Dispose();
        }
        catch
        {
        }

        try
        {
            if (!Process.HasExited)
            {
                Process.Kill();
            }
        }
        catch
        {
        }
        finally
        {
            Process.Dispose();
        }
    }
}

internal sealed record PlcsimAdvancedV6WorkerBootstrapResult(
    PlcsimAdvancedV6WorkerConnection? Connection,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Connection is not null && Diagnostics.Count == 0;

    public static PlcsimAdvancedV6WorkerBootstrapResult SucceededResult(
        PlcsimAdvancedV6WorkerConnection connection,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6WorkerBootstrapResult(
            connection ?? throw new ArgumentNullException(nameof(connection)),
            Array.Empty<PlcsimDiagnostic>(),
            evidence);
    }

    public static PlcsimAdvancedV6WorkerBootstrapResult Failed(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6WorkerBootstrapResult(
            null,
            new[] { diagnostic },
            evidence);
    }
}
