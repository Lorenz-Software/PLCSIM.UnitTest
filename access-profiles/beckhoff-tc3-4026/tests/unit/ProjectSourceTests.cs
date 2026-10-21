using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Logging;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class ProjectSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-source-tests", Guid.NewGuid().ToString("N"));
    private AccessProfileContext Context => new(NullLoggerFactory.Instance, TimeProvider.System,
        Path.Combine(root, "package"), Path.Combine(root, "data"),
        new OperationalLoggingOptions(Path.Combine(root, "logs"), LogLevel.Information, 1048576, 2));

    [Theory]
    [InlineData("3.1.4026.26", AccessProfileProjectSourceEvaluationState.Supported)]
    [InlineData("3.1.4026.99", AccessProfileProjectSourceEvaluationState.Supported)]
    [InlineData("3.1.4024.78", AccessProfileProjectSourceEvaluationState.Unsupported)]
    [InlineData("3.1.4028.1", AccessProfileProjectSourceEvaluationState.Unsupported)]
    public async Task SolutionEvaluationUsesPersistedCapabilityAndMetadataNotAnEnvironmentProbe(string version, AccessProfileProjectSourceEvaluationState expected)
    {
        await SaveSyntheticUsableRegistration();
        var source = WriteSolution(version);
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        await using var plugin = new BeckhoffAccessProfilePlugin(new RegistrationTests.Probe(_ => throw new InvalidOperationException("No probing during selection.")));
        plugin.Initialize(Context);
        Assert.Equal(expected, plugin.EvaluateProjectSource(source).State);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var item in before) Assert.Equal(item.Value, File.ReadAllBytes(item.Key));
    }

    [Fact]
    public async Task ArchiveEvaluationReadsSystemMetadataWithoutExtractingFiles()
    {
        await SaveSyntheticUsableRegistration();
        var source = Path.Combine(root, "project.tszip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            AddEntry(archive, "project.tsproj", SystemProject("3.1.4026.26"));
            AddEntry(archive, "Sources/Unrelated.TcPOU", "PLC source is not evaluated.");
        }
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        await using var plugin = new BeckhoffAccessProfilePlugin(new RegistrationTests.Probe(_ => throw new InvalidOperationException()));
        plugin.Initialize(Context);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Supported, plugin.EvaluateProjectSource(source).State);
        Assert.Equal(files, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        Assert.False(Directory.Exists(Path.Combine(root, "Sources")));
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("missing-system-project")]
    [InlineData("invalid-header")]
    [InlineData("invalid-xml")]
    [InlineData("missing-version")]
    [InlineData("external-entity")]
    [InlineData("not-a-zip")]
    [InlineData("duplicate-archive-entry")]
    public async Task CorruptOrUnavailableSourceReturnsError(string scenario)
    {
        await SaveSyntheticUsableRegistration();
        var source = WriteSolution("3.1.4026.26");
        var system = Path.Combine(root, "System Project.tsproj");
        switch (scenario)
        {
            case "missing-source": File.Delete(source); break;
            case "missing-system-project": File.Delete(system); break;
            case "invalid-header": File.WriteAllText(source, "Not a solution"); break;
            case "invalid-xml": File.WriteAllText(system, "<broken>"); break;
            case "missing-version": File.WriteAllText(system, "<TcSmProject><Project /></TcSmProject>"); break;
            case "external-entity": File.WriteAllText(system, "<!DOCTYPE TcSmProject [<!ENTITY external SYSTEM 'file:///not-read'>]><TcSmProject TcVersion='3.1.4026.26'><Project>&external;</Project></TcSmProject>"); break;
            case "not-a-zip": source = Path.Combine(root, "project.tszip"); File.WriteAllText(source, "Not a zip"); break;
            case "duplicate-archive-entry":
                source = Path.Combine(root, "project.tszip");
                using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
                {
                    AddEntry(archive, "project.tsproj", SystemProject("3.1.4026.26"));
                    AddEntry(archive, "PROJECT.TSPROJ", SystemProject("3.1.4026.26"));
                }
                break;
        }
        await using var plugin = new BeckhoffAccessProfilePlugin(new RegistrationTests.Probe(_ => throw new InvalidOperationException()));
        plugin.Initialize(Context);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, plugin.EvaluateProjectSource(source).State);
    }

    [Fact]
    public void UnrelatedSolutionsArchivesAndStandalonePlcProjectsAreUnsupported()
    {
        Directory.CreateDirectory(root);
        var solution = Path.Combine(root, "plain.sln");
        File.WriteAllText(solution, "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, BeckhoffProjectSourceInspector.Inspect(solution).State);
        var archivePath = Path.Combine(root, "plain.tszip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)) AddEntry(archive, "project.plcproj", "<Project />");
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, BeckhoffProjectSourceInspector.Inspect(archivePath).State);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, BeckhoffProjectSourceInspector.Inspect("project.plcproj").State);
    }

    private async Task SaveSyntheticUsableRegistration()
    {
        var environment = new BeckhoffEnvironment(Path.Combine(root, "TcXaeShell.exe"),
            [new(Path.Combine(root, "TwinCAT System Manager.x64.dll"), "3.1.4026.26")],
            [new(Path.Combine(root, "TcSystemServiceUm.exe"), "3.1.4026.26")]);
        await new BeckhoffProfileRegistryStore().SaveAsync(Context,
            BeckhoffProfileRegistry.Create(Context, BeckhoffRegistration.CreateResult(environment), environment), default);
    }

    private string WriteSolution(string version)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "System Project.tsproj"), SystemProject(version));
        var path = Path.Combine(root, "project.sln");
        File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}\") = \"System\", \"System Project.tsproj\", \"{0684F38A-C466-4A6C-8EBF-1AEFEE1862A7}\"\nEndProject\n");
        return path;
    }

    private static string SystemProject(string version) => $"<TcSmProject TcVersion=\"{version}\"><Project><Plc /></Project></TcSmProject>";
    private static void AddEntry(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
