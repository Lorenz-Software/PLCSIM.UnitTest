using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V19;

public sealed class TiaOpennessV19PackageTests
{
    [Fact]
    public void Manifest_matches_binding_identity_and_declaration()
    {
        var binding = new TiaOpennessV19Binding();
        using var manifest = JsonDocument.Parse(File.ReadAllText(FindManifest()));
        var root = manifest.RootElement;

        Assert.Equal("2.0.0", root.GetProperty("info").GetProperty("bindingVersion").GetString());
        Assert.Equal(binding.Info.DisplayName, root.GetProperty("info").GetProperty("displayName").GetString());
        Assert.Equal(typeof(TiaOpennessV19Binding).FullName, root.GetProperty("entryPoint").GetProperty("typeName").GetString());
        Assert.Equal(
            new[] { ".ap19", ".zap19" },
            binding.Declaration.ProjectSources.Select(source => source.Extension).ToArray());
    }

    [Fact]
    public void Package_copies_net48_worker_next_to_facade()
    {
        var facadeDirectory = Path.GetDirectoryName(typeof(TiaOpennessV19Binding).Assembly.Location)!;
        var worker = Path.Combine(
            facadeDirectory,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.Worker.exe");

        Assert.True(File.Exists(worker), $"Expected deployed worker at '{worker}'.");
    }

    [Fact]
    public async Task Worker_reports_missing_public_api_over_versioned_pipe_protocol()
    {
        var worker = Path.Combine(
            Path.GetDirectoryName(typeof(TiaOpennessV19Binding).Assembly.Location)!,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.Worker.exe");
        var pipeName = "PLCSIM.UnitTest.TiaOpenness.V19.Test." + Guid.NewGuid().ToString("N");
        var logDirectory = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "tia-v19-worker-tests");
        var startInfo = new ProcessStartInfo(worker)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddArgument(startInfo, "--pipe", pipeName);
        AddArgument(startInfo, "--access-profile-id", "siemens-test");
        AddArgument(startInfo, "--worker-instance-id", Guid.NewGuid().ToString("N"));
        AddArgument(startInfo, "--log-directory", logDirectory);
        AddArgument(startInfo, "--log-level", "Information");
        AddArgument(startInfo, "--log-file-size-limit-bytes", "1048576");
        AddArgument(startInfo, "--log-retained-file-count", "2");

        using var process = Process.Start(startInfo)!;
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, true, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

        var response = await reader.ReadLineAsync();
        Assert.StartsWith(
            "SIEMENS-WORKER/1|type=Error|code=TiaOpenness.V19.Worker.PublicApiMissing",
            response);

        await process.WaitForExitAsync();
        Assert.Equal(2, process.ExitCode);
    }

    private static string FindManifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "tia-openness",
                "v19",
                "src",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19",
                SiemensBindingPackageDescriptorContract.FileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("TIA Openness V19 binding manifest was not found.");
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }
}
