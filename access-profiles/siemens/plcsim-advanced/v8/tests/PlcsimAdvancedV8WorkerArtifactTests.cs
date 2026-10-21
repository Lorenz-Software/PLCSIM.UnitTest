using System.Text;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class PlcsimAdvancedV8WorkerArtifactTests
{
    [Fact]
    public void Performance_measurement_worker_is_deployed_beside_the_tests()
    {
        ReadArtifact(Path.Combine(AppContext.BaseDirectory,
            "PLCSIM.UnitTest.PlcsimAdvanced.V8.PerformanceMeasurementWorker.exe"));
    }

    private const string MeasurementEnvironmentVariable =
        "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_TARGET";
    private const string MeasurementDeferredState = "PayloadReadDeferred";

    [Fact]
    public void Production_worker_excludes_performance_measurement_instrumentation()
    {
        var worker = ReadArtifact(ResolveWorkerPath(
            "worker",
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker",
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe"));

        Assert.False(ContainsText(worker, MeasurementEnvironmentVariable));
        Assert.False(ContainsText(worker, MeasurementDeferredState));
    }

    [Fact]
    public void Performance_measurement_worker_contains_measurement_instrumentation()
    {
        var worker = ReadArtifact(ResolveWorkerPath(
            "tests",
            "worker",
            "PerformanceMeasurementWorker",
            "PLCSIM.UnitTest.PlcsimAdvanced.V8.PerformanceMeasurementWorker.exe"));

        Assert.True(ContainsText(worker, MeasurementEnvironmentVariable));
        Assert.True(ContainsText(worker, MeasurementDeferredState));
    }

    private static byte[] ReadArtifact(string path)
    {
        Assert.True(File.Exists(path), $"Worker artifact was not found: {path}");
        return File.ReadAllBytes(path);
    }

    private static string ResolveWorkerPath(params string[] pathSegments)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateSegments = new[]
            {
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v8"
            }
                .Concat(pathSegments[..^1])
                .Concat(new[] { "bin", configuration, "net48", pathSegments[^1] })
                .ToArray();
            var candidate = Path.Combine(candidateSegments);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, pathSegments[^1]);
    }

    private static bool ContainsText(byte[] source, string value)
    {
        return ContainsSequence(source, Encoding.UTF8.GetBytes(value))
            || ContainsSequence(source, Encoding.Unicode.GetBytes(value));
    }

    private static bool ContainsSequence(byte[] source, byte[] value)
    {
        for (var sourceIndex = 0; sourceIndex <= source.Length - value.Length; sourceIndex++)
        {
            var matches = true;
            for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
            {
                if (source[sourceIndex + valueIndex] == value[valueIndex])
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }
}
