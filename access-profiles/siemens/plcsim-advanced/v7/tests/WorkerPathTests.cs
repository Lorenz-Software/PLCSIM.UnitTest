using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7;

public sealed class WorkerPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Worker_matches_the_test_build_configuration(bool validation)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var path = validation
            ? PlcsimAdvancedV7RealRuntimeTests.ResolveBuiltValidationWorkerPath()
            : PlcsimAdvancedV7RealRuntimeTests.ResolveBuiltWorkerPath();
        var name = validation
            ? "PLCSIM.UnitTest.PlcsimAdvanced.V7.ValidationWorker.exe"
            : "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker.exe";

        Assert.True(File.Exists(path), path);
        Assert.EndsWith(Path.Combine("bin", configuration, "net48", name), path,
            StringComparison.OrdinalIgnoreCase);
    }
}
