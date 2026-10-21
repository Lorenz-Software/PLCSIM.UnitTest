using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;
using PLCSIM.UnitTest.Common.Logging;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V17;

public sealed class TiaOpennessV17WorkerHostTests
{
    [Fact]
    public void V17_worker_start_arguments_include_operational_logging_options()
    {
        var logging = new OperationalLoggingOptions(
            Path.Combine(Path.GetTempPath(), "PLCSIM UnitTest Logs"),
            LogLevel.Trace,
            1_234_567,
            7);
        var context = new SiemensBindingContext("siemens-test", logging);

        var startInfo = TiaOpennessV17WorkerProcessHost.CreateWorkerStartInfo(
            "worker.exe",
            "test-pipe",
            "public-api",
            "sample.ap17",
            TiaProjectOpenMode.DirectProject,
            null,
            context,
            "worker-42");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(new[]
        {
            "--pipe", "test-pipe",
            "--public-api", "public-api",
            "--project-source", "sample.ap17",
            "--project-open-mode", "DirectProject",
            "--access-profile-id", "siemens-test",
            "--worker-instance-id", "worker-42",
            "--log-directory", logging.LogDirectory,
            "--log-level", "Trace",
            "--log-file-size-limit-bytes", "1234567",
            "--log-retained-file-count", "7"
        }, startInfo.ArgumentList);
    }

    [Fact]
    public void V17_archive_worker_start_arguments_include_open_mode_and_work_base()
    {
        var context = new SiemensBindingContext("siemens-test", TiaOpennessV17TestSupport.DefaultOperationalLogging);
        var archiveWorkDirectory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "archive-work");

        var startInfo = TiaOpennessV17WorkerProcessHost.CreateWorkerStartInfo(
            "worker.exe",
            "test-pipe",
            "public-api",
            "sample.zap17",
            TiaProjectOpenMode.RetrievedArchive,
            archiveWorkDirectory,
            context,
            "worker-42");

        Assert.Contains("RetrievedArchive", startInfo.ArgumentList);
        var workDirectoryArgument = startInfo.ArgumentList.IndexOf("--archive-work-directory");
        Assert.True(workDirectoryArgument >= 0);
        Assert.Equal(archiveWorkDirectory, startInfo.ArgumentList[workDirectoryArgument + 1]);
    }


    [Fact]
    public async Task V17_binding_logs_correlated_worker_start_ipc_api_failure_and_exit()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(testRoot, "logs");
        var projectSource = Path.Combine(testRoot, "sample.ap17");
        Directory.CreateDirectory(testRoot);
        File.WriteAllText(projectSource, string.Empty);
        var publicApiDirectory = Path.Combine(testRoot, "missing-public-api");
        var probe = new RecordingV17EnvironmentProbe(TiaOpennessV17EnvironmentProbeResult.Ready(
            publicApiDirectory,
            new[]
        {
            new TiaEvidence(
                "tia-openness-v17-public-api",
                "PublicApiDirectory",
                publicApiDirectory)
        }));
        var binding = new TiaOpennessV17Binding(probe);
        binding.Initialize(new SiemensBindingContext(
            "siemens-worker-log-test",
            CreateWorkerLoggingOptions(logDirectory, LogLevel.Debug)));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Null(open.Session);
        Assert.Equal(TiaOperationStatus.Failed, open.Status);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V17.Worker.EngineeringAssemblyMissing");
        Assert.Contains(open.Evidence, evidence => evidence.Id == "tia-openness-v17-public-api");
        Assert.True(probe.WasCalled);

        var log = await ReadWorkerLogAsync(
            logDirectory,
            "siemens-worker-log-test",
            "TIA Openness V17 worker exiting. ExitCode=3");
        Assert.Contains("TIA Openness V17 worker started.", log);
        Assert.Contains("Named-pipe client connected.", log);
        Assert.Contains("TIA Openness PublicAPI directory resolved.", log);
        Assert.Contains("DiagnosticCode=TiaOpenness.V17.Worker.EngineeringAssemblyMissing", log);
        Assert.Contains("TIA Openness V17 worker exiting. ExitCode=3", log);
        Assert.Contains("siemens-worker-log-test/tia-openness/v17/", log);
        Assert.Contains("/PID:", log);
        Assert.DoesNotContain("SIEMENS-WORKER/1|", log);
    }

    [Fact]
    public async Task V17_worker_keeps_stderr_fallback_when_operational_logger_initialization_fails()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var projectSource = Path.Combine(testRoot, "sample.ap17");
        File.WriteAllText(projectSource, string.Empty);
        var invalidLogDirectory = Path.Combine(testRoot, "log-directory-is-a-file");
        File.WriteAllText(invalidLogDirectory, string.Empty);
        var publicApiDirectory = Path.Combine(testRoot, "missing-public-api");
        var probe = new RecordingV17EnvironmentProbe(TiaOpennessV17EnvironmentProbeResult.Ready(
            publicApiDirectory,
            new[]
        {
            new TiaEvidence(
                "tia-openness-v17-public-api",
                "PublicApiDirectory",
                publicApiDirectory)
        }));
        var binding = new TiaOpennessV17Binding(probe);
        binding.Initialize(new SiemensBindingContext(
            "siemens-worker-fallback-test",
            CreateWorkerLoggingOptions(invalidLogDirectory, LogLevel.Information)));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Contains(open.Diagnostics, diagnostic => diagnostic.Code == "TiaOpenness.V17.Worker.EngineeringAssemblyMissing");
        Assert.Contains(
            open.Evidence,
            evidence => evidence.Kind == "EngineeringSessionWorkerError"
                && evidence.Summary.Contains("Operational logger initialization failed", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(testRoot, "access-profile-*-tia-openness-v17-worker-*.log"));
    }

    [Fact]
    public async Task V17_worker_flushes_unexpected_api_load_exception_before_host_cleanup()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"));
        var publicApiDirectory = Path.Combine(testRoot, "PublicAPI");
        var logDirectory = Path.Combine(testRoot, "logs");
        Directory.CreateDirectory(publicApiDirectory);
        File.WriteAllText(Path.Combine(publicApiDirectory, "Siemens.Engineering.dll"), "not an assembly");
        var projectSource = Path.Combine(testRoot, "sample.ap17");
        File.WriteAllText(projectSource, string.Empty);
        var probe = new RecordingV17EnvironmentProbe(TiaOpennessV17EnvironmentProbeResult.Ready(
            publicApiDirectory,
            new[]
        {
            new TiaEvidence(
                "tia-openness-v17-public-api",
                "PublicApiDirectory",
                publicApiDirectory)
        }));
        var binding = new TiaOpennessV17Binding(probe);
        binding.Initialize(new SiemensBindingContext(
            "siemens-worker-exception-test",
            CreateWorkerLoggingOptions(logDirectory, LogLevel.Debug)));

        var open = await binding.OpenProjectAsync(
            new TiaProjectOpenRequest(
                projectSource,
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Contains(
            open.Diagnostics,
            diagnostic => diagnostic.Code == "TiaOpenness.V17.EngineeringSessionWorkerNoResponse");

        var log = await ReadWorkerLogAsync(
            logDirectory,
            "siemens-worker-exception-test",
            "TIA Openness V17 worker exiting. ExitCode=1");
        Assert.Contains("TIA Openness worker failed.", log);
        Assert.Contains("BadImageFormatException", log);
        Assert.Contains("TIA Openness V17 worker exiting. ExitCode=1", log);
    }

    [Fact]
    public async Task V17_engineering_session_host_honors_pre_cancelled_open_without_starting_worker()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logDirectory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", Guid.NewGuid().ToString("N"), "logs");
        var host = new TiaOpennessV17EngineeringSessionHost();

        await Assert.ThrowsAsync<OperationCanceledException>(() => host.OpenAsync(
            "unused-public-api",
            new TiaProjectOpenRequest(
                "unused.ap17",
                TiaProjectOpenMode.DirectProject,
                null,
                null),
            new SiemensBindingContext(
                "siemens-worker-cancel-test",
                CreateWorkerLoggingOptions(logDirectory, LogLevel.Debug)),
            cancellation.Token));

        Assert.False(Directory.Exists(logDirectory));
    }

    [Fact]
    public async Task V17_engineering_session_host_honors_pre_cancelled_archive_without_allocating_workspace()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "archive-cancel-tests",
            Guid.NewGuid().ToString("N"));
        var archiveBaseDirectory = Path.Combine(testRoot, "work");
        var host = new TiaOpennessV17EngineeringSessionHost();

        await Assert.ThrowsAsync<OperationCanceledException>(() => host.OpenAsync(
            "unused-public-api",
            new TiaProjectOpenRequest(
                Path.Combine(testRoot, "sample.zap17"),
                TiaProjectOpenMode.RetrievedArchive,
                archiveBaseDirectory,
                null),
            new SiemensBindingContext(
                "siemens-worker-archive-cancel-test",
                TiaOpennessV17TestSupport.DefaultOperationalLogging),
            cancellation.Token));

        Assert.False(Directory.Exists(archiveBaseDirectory));
    }


    private static OperationalLoggingOptions CreateWorkerLoggingOptions(
        string logDirectory,
        LogLevel minimumLevel)
    {
        return new OperationalLoggingOptions(
            logDirectory,
            minimumLevel,
            10 * 1024 * 1024,
            5);
    }

    private static async Task<string> ReadWorkerLogAsync(
        string logDirectory,
        string accessProfileId,
        string terminalMarker)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        var content = string.Empty;
        do
        {
            var logPaths = Directory.Exists(logDirectory)
                ? Directory.GetFiles(
                    logDirectory,
                    "access-profile-" + accessProfileId + "-tia-openness-v17-worker-*.log")
                : Array.Empty<string>();
            if (logPaths.Length == 1)
            {
                content = File.ReadAllText(logPaths[0]);
                if (content.Contains(terminalMarker, StringComparison.Ordinal))
                {
                    return content;
                }
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Contains(terminalMarker, content);
        return content;
    }

}
