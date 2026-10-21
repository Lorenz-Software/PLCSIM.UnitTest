using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;
using PLCSIM.UnitTest.Common.Logging;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V17;

public sealed class TiaOpennessV17FailedStartTests
{
    [Fact]
    public async Task Failed_start_waits_for_terminal_log_and_stderr_before_cleanup()
    {
        using var worker = await ControlledWorker.StartAsync();
        var completion = worker.Lifetime.CompleteFailedStartAsync(CancellationToken.None);

        Assert.NotSame(completion, await Task.WhenAny(completion, Task.Delay(100)));
        await worker.Process.StandardInput.WriteLineAsync("finish");
        await worker.Process.StandardInput.FlushAsync();
        var error = await completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("terminal-stderr", error);
        Assert.Equal("terminal-log", File.ReadAllText(worker.LogPath));
        Assert.True(worker.Lifetime.IsTerminated);
        Assert.True(worker.HasExited());
    }

    [Fact]
    public async Task Failed_start_kills_worker_that_does_not_exit_within_grace_period()
    {
        using var worker = await ControlledWorker.StartAsync();
        var elapsed = Stopwatch.StartNew();

        var error = await worker.Lifetime.CompleteFailedStartAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1));
        Assert.Contains("initial-stderr", error);
        Assert.True(worker.Lifetime.IsTerminated);
        Assert.True(worker.HasExited());
        Assert.False(File.Exists(worker.LogPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_start_cancellation_is_propagated_and_owned_process_is_terminated(bool preCancelled)
    {
        using var worker = await ControlledWorker.StartAsync();
        using var cancellation = new CancellationTokenSource();
        if (preCancelled)
        {
            cancellation.Cancel();
        }

        var completion = worker.Lifetime.CompleteFailedStartAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(worker.Lifetime.IsTerminated);
        Assert.True(worker.HasExited());
        Assert.False(File.Exists(worker.LogPath));
    }

    [Fact]
    public async Task Failed_start_accepts_already_exited_worker_and_retains_diagnostics()
    {
        using var worker = await ControlledWorker.StartAsync();
        await worker.Process.StandardInput.WriteLineAsync("finish");
        await worker.Process.StandardInput.FlushAsync();
        await worker.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var completion = worker.Lifetime.CompleteFailedStartAsync(CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Contains("terminal-stderr", await completion);
        Assert.True(worker.Lifetime.IsTerminated);
    }

    [Fact]
    public async Task Authorization_error_keeps_diagnostic_and_flushes_terminal_worker_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "authorization-log", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await new TiaOpennessV17AuthorizationProbeHost().ProbeAsync(
                Path.Combine(root, "missing-public-api"),
                new SiemensBindingContext(
                    "authorization-log-test",
                    new OperationalLoggingOptions(
                        root, LogLevel.Debug, 1024 * 1024, 2)),
                CancellationToken.None);

            Assert.Contains(result.Diagnostics,
                diagnostic => diagnostic.Code == "TiaOpenness.V17.Worker.EngineeringAssemblyMissing");
            var log = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.log")));
            Assert.Contains("TIA Openness V17 worker exiting. ExitCode=3", log);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ControlledWorker : IDisposable
    {
        private readonly string root;
        private readonly int processId;

        private ControlledWorker(Process process, string root)
        {
            Process = process;
            processId = process.Id;
            this.root = root;
            LogPath = Path.Combine(root, "worker.log");
            var pipe = new NamedPipeClientStream(".", "unused-" + Guid.NewGuid().ToString("N"));
            Lifetime = new TiaOpennessV17WorkerProcessLifetime(
                process, pipe, new StreamReader(pipe), new StreamWriter(pipe),
                TiaOpennessV17BoundedProcessErrorCapture.Start(process));
        }

        public Process Process { get; }
        public string LogPath { get; }
        public TiaOpennessV17WorkerProcessLifetime Lifetime { get; }

        public static async Task<ControlledWorker> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "failed-start", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // The input gate holds terminal diagnostics until the host has begun cleanup.
            const string script = "[Console]::Error.WriteLine('initial-stderr'); "
                + "[Console]::Out.WriteLine('error-response'); [Console]::Out.Flush(); "
                + "$null = [Console]::ReadLine(); "
                + "[IO.File]::WriteAllText($env:TEST_WORKER_LOG, 'terminal-log'); "
                + "[Console]::Error.WriteLine('terminal-stderr'); exit 3";
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            info.Environment["TEST_WORKER_LOG"] = Path.Combine(root, "worker.log");
            ControlledWorker? worker = null;
            try
            {
                worker = new ControlledWorker(Process.Start(info)
                    ?? throw new InvalidOperationException("Could not start controlled worker."), root);
                Assert.Equal("error-response",
                    await worker.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                return worker;
            }
            catch
            {
                if (worker is not null)
                {
                    worker.Dispose();
                }
                else
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        public bool HasExited()
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            Lifetime.Terminate();
            Directory.Delete(root, recursive: true);
        }
    }
}
