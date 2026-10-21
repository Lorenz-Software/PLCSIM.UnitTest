using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class PlcsimAdvancedV8PerformanceHarnessTests
{
    [Fact]
    public async Task Performance_harness_executes_the_production_snapshot_path_and_emits_json()
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = "--performance --workload events-16 --warmup 1 --iterations 2 --monitoring-interval-ms 50 --drain-interval-ms 2 --max-events-per-batch 32",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The V8 validation worker could not be started.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, error);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal("1.1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(50, root.GetProperty("monitoringIntervalMs").GetInt32());
        Assert.Equal(2, root.GetProperty("drainIntervalMs").GetInt32());
        Assert.Equal(32, root.GetProperty("maxEventsPerBatch").GetInt32());
        Assert.False(root.GetProperty("rawEventLoggingEnabled").GetBoolean());

        var workload = root.GetProperty("workloads")[0];
        Assert.Equal("events-16", workload.GetProperty("name").GetString());
        Assert.Equal(16, workload.GetProperty("capturedEvents").GetInt32());
        Assert.Equal(53, workload.GetProperty("readCalls").GetInt32());
        Assert.Equal(1, workload.GetProperty("writeCalls").GetInt32());
        Assert.Equal(54, workload.GetProperty("runtimeApiCalls").GetInt32());
        Assert.Equal(2, workload.GetProperty("releaseApiCalls").GetInt32());
        Assert.Equal(5, workload.GetProperty("numericReadCalls").GetInt32());
        Assert.Equal(48, workload.GetProperty("stringReadCalls").GetInt32());
        Assert.Equal("Valid", workload.GetProperty("readState").GetString());
        Assert.True(workload.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Performance_harness_models_bounded_adaptive_drain()
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = "--performance --workload adaptive-drain --warmup 1 --iterations 2 --monitoring-interval-ms 10 --drain-interval-ms 2 --max-events-per-batch 32",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The V8 validation worker could not be started.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, error);

        using var document = JsonDocument.Parse(output);
        var workloads = document.RootElement.GetProperty("workloads");
        Assert.Equal(2, workloads.GetArrayLength());
        Assert.Equal("empty", workloads[0].GetProperty("name").GetString());

        var drain = workloads[1];
        Assert.Equal("drain-256", drain.GetProperty("name").GetString());
        Assert.Equal(8, drain.GetProperty("releasedBatchCount").GetInt32());
        Assert.Equal(16, drain.GetProperty("releaseApiCalls").GetInt32());
        Assert.Equal(16, drain.GetProperty("modeledReleaseWaitMs").GetInt32());
        Assert.Equal(8, drain.GetProperty("modeledCommandOpportunityPoints").GetInt32());
        Assert.Equal(256, drain.GetProperty("capturedEvents").GetInt32());
        Assert.Equal(0, drain.GetProperty("lostEvents").GetInt32());
    }

    [Fact]
    public async Task Performance_harness_can_measure_raw_event_log_formatting()
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = "--performance --workload events-16 --warmup 1 --iterations 2 --poll-interval-ms 50 --raw-event-logging",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The V8 validation worker could not be started.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, error);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.True(root.GetProperty("rawEventLoggingEnabled").GetBoolean());
        Assert.True(root.GetProperty("workloads")[0].GetProperty("succeeded").GetBoolean());
    }

    private static string ResolveBuiltValidationWorkerPath()
    {
        var configuration = AppContext.BaseDirectory
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault(part => string.Equals(part, "Release", StringComparison.OrdinalIgnoreCase))
            ?? "Debug";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v8",
                "tests",
                "worker",
                "ValidationWorker",
                "bin",
                configuration,
                "net48",
                "PLCSIM.UnitTest.PlcsimAdvanced.V8.ValidationWorker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.PlcsimAdvanced.V8.ValidationWorker.exe");
    }
}
