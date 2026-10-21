using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class XaeBuildCallTests
{
    [Fact]
    public void BuildsSelectedSystemWithExistingConfigurationAndWaits()
    {
        var host = new FakeHost();
        var result = XaeProjectAccess.ExecuteBuild(host, Target(), default);
        Assert.True(result.Succeeded);
        Assert.Equal(("Custom|TwinCAT OS (x64)", Target().SystemProjectPath, true), Assert.Single(host.Solution.SolutionBuild.Calls));
        Assert.Equal(LogLevel.Information, Assert.Single(result.Messages).Level);
    }

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, -1, false)]
    [InlineData(1, 0, false)]
    [InlineData(2, 0, false)]
    [InlineData(99, 0, false)]
    public void ResultDependsOnlyOnCompletionAndFailureCount(int state, int failed, bool succeeds)
    {
        var host = new FakeHost();
        host.Solution.SolutionBuild.FinalState = state;
        host.Solution.SolutionBuild.FailedProjects = failed;
        var result = XaeProjectAccess.ExecuteBuild(host, Target(), default);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.Equal(state == 3 ? 1 : 0, host.Solution.SolutionBuild.LastBuildInfoReads);
        if (!succeeds) Assert.Contains("TwinCAT XAE", Assert.Single(result.Messages).Message);
    }

    [Fact]
    public void DebugOutputDoesNotBecomeDiagnosticsAndIsBounded()
    {
        var logger = new TestLogger();
        var result = XaeProjectAccess.ExecuteBuild(new FakeHost(), Target(), default, logger,
            () => new string('x', XaeProjectAccess.MaxBuildOutputCharacters + 100));
        Assert.True(result.Succeeded);
        Assert.Single(result.Messages);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal(XaeProjectAccess.MaxBuildOutputCharacters, entry.Text.Count(c => c == 'x'));
    }

    [Fact]
    public void DisabledDebugLoggingDoesNotReadOutput()
    {
        var logger = new TestLogger { Enabled = false };
        var reads = 0;
        var result = XaeProjectAccess.ExecuteBuild(new FakeHost(), Target(), default, logger, () => { reads++; return "output"; });
        Assert.True(result.Succeeded);
        Assert.Equal(0, reads);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UnavailableOutputCannotChangeBuildOutcome(int failed)
    {
        var host = new FakeHost();
        host.Solution.SolutionBuild.FailedProjects = failed;
        var logger = new TestLogger();
        var result = XaeProjectAccess.ExecuteBuild(host, Target(), default, logger, () => throw new COMException("No pane"));
        Assert.Equal(failed == 0, result.Succeeded);
        Assert.Single(result.Messages);
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void BrokenLogProviderCannotChangeBuildOutcome()
    {
        var result = XaeProjectAccess.ExecuteBuild(new FakeHost(), Target(), default,
            new TestLogger { Throw = true }, () => "compiler output");
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void VendorExceptionRetainsHresult()
    {
        var host = new FakeHost();
        host.Solution.SolutionBuild.OnBuild = () => throw new COMException("Vendor failure", unchecked((int)0x80004005));
        var result = XaeProjectAccess.ExecuteBuild(host, Target(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("80004005", Assert.Single(result.Messages).Message);
    }

    [Fact]
    public void CancellationBeforeBuildSkipsCallAndDuringBuildRetainsResult()
    {
        var host = new FakeHost();
        Assert.ThrowsAny<OperationCanceledException>(() => XaeProjectAccess.ExecuteBuild(host, Target(), new(true)));
        Assert.Empty(host.Solution.SolutionBuild.Calls);
        using var cancellation = new CancellationTokenSource();
        host.Solution.SolutionBuild.OnBuild = cancellation.Cancel;
        Assert.True(XaeProjectAccess.ExecuteBuild(host, Target(), cancellation.Token).Succeeded);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyOrUnconfiguredBuildDoesNotStart(bool busy)
    {
        var host = new FakeHost();
        if (busy) host.Solution.SolutionBuild.BuildState = 2;
        else host.Solution.SolutionBuild.ActiveConfiguration.PlatformName = "";
        Assert.False(XaeProjectAccess.ExecuteBuild(host, Target(), default).Succeeded);
        Assert.Empty(host.Solution.SolutionBuild.Calls);
    }

    private static EngineeringPlcTarget Target() => EngineeringPlcTarget.FromXml(Path.GetTempPath(),
        Path.Combine(Path.GetTempPath(), "selected.tsproj"), "System", EngineeringFixture.PlcXml());

    public sealed class FakeHost { public FakeSolution Solution { get; } = new(); }
    public sealed class FakeSolution { public FakeBuild SolutionBuild { get; } = new(); }
    public sealed class FakeConfiguration : IXaeSolutionConfiguration
    {
        public string Name { get; set; } = "Custom";
        public string PlatformName { get; set; } = "TwinCAT OS (x64)";
    }
    public sealed class FakeBuild
    {
        public FakeConfiguration ActiveConfiguration { get; } = new();
        public int BuildState { get; set; } = 1;
        public int FinalState { get; set; } = 3;
        public int FailedProjects { get; set; }
        public int LastBuildInfoReads { get; private set; }
        public int LastBuildInfo { get { LastBuildInfoReads++; return FailedProjects; } }
        public Action? OnBuild { get; set; }
        public List<(string, string, bool)> Calls { get; } = [];
        public void BuildProject(string config, string path, bool wait) { Calls.Add((config,path,wait)); OnBuild?.Invoke(); BuildState=FinalState; }
    }
    private sealed class TestLogger : ILogger
    {
        public bool Enabled { get; init; } = true;
        public bool Throw { get; init; }
        public List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => Enabled;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (Throw) throw new IOException("Logger failed"); Entries.Add((level,formatter(state,exception))); }
    }
}
