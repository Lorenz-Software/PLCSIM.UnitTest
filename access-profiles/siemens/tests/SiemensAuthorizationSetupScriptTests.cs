using System.Diagnostics;
using System.Text.Json;
using PLCSIM.UnitTest.AccessProfiles.Siemens;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed class SiemensAuthorizationSetupScriptTests
{
    [Fact]
    public async Task Help_reports_usage_and_succeeds()
    {
        var result = await RunScriptAsync("-Help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-Path", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-All", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHelp_reports_comment_based_help()
    {
        var scriptPath = GetScriptPath().Replace("'", "''", StringComparison.Ordinal);
        var result = await RunPowerShellCommandAsync(
            $"Get-Help '{scriptPath}' -Full | Out-String -Width 120");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Authorizes deployed real TIA Openness binding workers",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("interactive list is shown", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Path_and_all_are_rejected_together()
    {
        var result = await RunScriptAsync("-Path", "missing", "-All");

        Assert.Equal(10, result.ExitCode);
        Assert.Contains("mutually exclusive", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_package_is_rejected()
    {
        var result = await RunScriptAsync("-Path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Equal(20, result.ExitCode);
        Assert.Contains("does not exist", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Entry_assembly_outside_package_is_rejected()
    {
        var root = CreateTempDirectory();
        try
        {
            var package = Path.Combine(root, "binding");
            Directory.CreateDirectory(package);
            File.WriteAllText(
                Path.Combine(package, "manifest.json"),
                """
                {
                  "schemaVersion": 1,
                  "info": {
                    "bindingKind": "tia-openness",
                    "bindingVersion": "test",
                    "displayName": "Invalid binding"
                  },
                  "entryPoint": {
                    "assembly": "../outside.dll",
                    "typeName": "Binding"
                  },
                  "setup": {
                    "tiaOpennessAuthorizationProbe": true
                  }
                }
                """);

            var result = await RunScriptAsync("-Path", package);

            Assert.Equal(20, result.ExitCode);
            Assert.Contains("outside package", result.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task No_selection_argument_uses_interactive_package_selection()
    {
        var root = CreateTempDirectory();
        try
        {
            StageProfilePackage(root);
            var bindingsRoot = Path.Combine(root, "lib", "bindings");
            CreatePackage(bindingsRoot, "authorized", typeof(AuthorizedProbeBinding), advertisesProbe: true);

            var result = await RunInteractivePowerShellAsync(
                Path.Combine(root, "setup-siemens-openness.ps1"),
                "1");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Available TIA Openness binding packages", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Result: Authorized", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task All_excludes_package_without_authorization_capability()
    {
        var root = CreateTempDirectory();
        try
        {
            var scriptPath = Path.Combine(root, "setup-siemens-openness.ps1");
            File.Copy(GetScriptPath(), scriptPath);
            CreatePackage(root, "tia-openness-fake", typeof(AuthorizedProbeBinding), advertisesProbe: false);

            var result = await RunPowerShellAsync(scriptPath, "-All");

            Assert.Equal(20, result.ExitCode);
            Assert.Contains("No TIA Openness binding package", result.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(typeof(AuthorizedProbeBinding), 0, "Authorized")]
    [InlineData(typeof(UnavailableProbeBinding), 21, "Unavailable")]
    [InlineData(typeof(DeniedProbeBinding), 30, "Denied")]
    [InlineData(typeof(TimedOutProbeBinding), 31, "TimedOut")]
    [InlineData(typeof(FailedProbeBinding), 30, "Failed")]
    public async Task Probe_status_maps_to_documented_exit_code(
        Type bindingType,
        int expectedExitCode,
        string expectedStatus)
    {
        var root = CreateTempDirectory();
        try
        {
            var package = CreatePackage(root, "binding", bindingType, advertisesProbe: true);

            var result = await RunScriptAsync("-Path", package);

            Assert.True(
                result.ExitCode == expectedExitCode,
                $"Expected exit code {expectedExitCode}, got {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
            Assert.Contains($"Result: {expectedStatus}", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Multiple_explicit_packages_are_all_processed_and_most_severe_exit_code_wins()
    {
        var root = CreateTempDirectory();
        try
        {
            StageProfilePackage(root);
            var bindingsRoot = Path.Combine(root, "lib", "bindings");
            CreatePackage(bindingsRoot, "authorized", typeof(AuthorizedProbeBinding), advertisesProbe: true);
            CreatePackage(bindingsRoot, "timed-out", typeof(TimedOutProbeBinding), advertisesProbe: true);
            var result = await RunPowerShellAsync(
                Path.Combine(root, "setup-siemens-openness.ps1"),
                "-All");

            Assert.True(
                result.ExitCode == 31,
                $"Expected exit code 31, got {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
            Assert.Equal(2, CountOccurrences(result.StandardOutput, "Probing "));
            Assert.Contains("Result: Authorized", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Result: TimedOut", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Probe_binding_is_disposed_after_success()
    {
        var root = CreateTempDirectory();
        var marker = Path.Combine(root, "disposed.marker");
        try
        {
            var package = CreatePackage(root, "binding", typeof(AuthorizedProbeBinding), advertisesProbe: true);
            var result = await RunScriptAsync(
                new Dictionary<string, string?> { [ProbeBindingBase.DisposeMarkerVariable] = marker },
                "-Path",
                package);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_failure_has_dedicated_exit_code()
    {
        var root = CreateTempDirectory();
        try
        {
            var package = CreatePackage(root, "binding", typeof(CleanupFailureProbeBinding), advertisesProbe: true);

            var result = await RunScriptAsync("-Path", package);

            Assert.Equal(50, result.ExitCode);
            Assert.Contains("Result: CleanupFailed", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePackage(
        string root,
        string name,
        Type bindingType,
        bool advertisesProbe)
    {
        var package = Path.Combine(root, name);
        Directory.CreateDirectory(package);
        var assemblySource = typeof(SiemensAuthorizationSetupScriptTests).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblySource);
        File.Copy(assemblySource, Path.Combine(package, assemblyName), overwrite: true);
        File.WriteAllText(
            Path.Combine(package, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                info = new
                {
                    bindingKind = "tia-openness",
                    bindingVersion = "test",
                    displayName = name
                },
                entryPoint = new
                {
                    assembly = assemblyName,
                    typeName = bindingType.FullName
                },
                setup = new
                {
                    tiaOpennessAuthorizationProbe = advertisesProbe
                }
            }));
        return package;
    }

    private static Task<ProcessResult> RunScriptAsync(params string[] arguments)
    {
        return RunScriptAsync(new Dictionary<string, string?>(), arguments);
    }

    private static Task<ProcessResult> RunScriptAsync(
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        return RunPowerShellAsync(GetScriptPath(), environment, arguments);
    }

    private static Task<ProcessResult> RunPowerShellAsync(
        string scriptPath,
        params string[] arguments)
    {
        return RunPowerShellAsync(scriptPath, new Dictionary<string, string?>(), arguments);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string scriptPath,
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return await RunProcessAsync(startInfo);
    }

    private static Task<ProcessResult> RunPowerShellCommandAsync(string command)
    {
        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        return RunProcessAsync(startInfo);
    }

    private static async Task<ProcessResult> RunInteractivePowerShellAsync(
        string scriptPath,
        string selection)
    {
        var startInfo = CreateStartInfo();
        startInfo.RedirectStandardInput = true;
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(selection);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        return new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string GetScriptPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(SiemensTiaOpennessAuthorizationSetupRunner).Assembly.Location)!,
            "setup-siemens-openness.ps1");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.SiemensAuthorizationSetupTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void StageProfilePackage(string destination)
    {
        var source = Path.GetDirectoryName(GetScriptPath())!;
        string[] files =
        [
            "setup-siemens-openness.ps1",
            "PLCSIM.UnitTest.AccessProfiles.Siemens.dll",
            "PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.dll",
            "PLCSIM.UnitTest.Common.dll",
            "PLCSIM.UnitTest.Common.Application.dll",
            "Microsoft.Extensions.Logging.Abstractions.dll"
        ];
        foreach (var file in files)
        {
            File.Copy(Path.Combine(source, file), Path.Combine(destination, file), overwrite: true);
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    public abstract class ProbeBindingBase : ISiemensTechnologyBinding, ITiaOpennessAuthorizationProbeBinding, IDisposable
    {
        public const string DisposeMarkerVariable = "PLCSIM_UNITTEST_AUTH_PROBE_DISPOSE_MARKER";

        protected abstract TiaAuthorizationProbeStatus Status { get; }

        public SiemensBindingInfo Info { get; } =
            new(SiemensBindingKinds.TiaOpenness, "test", "Test authorization probe binding");

        public SiemensBindingDeclaration Declaration => SiemensBindingDeclaration.Empty;

        public void Initialize(SiemensBindingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
        {
            return SiemensBindingReadinessResult.Usable("Test binding is ready.");
        }

        public Task<TiaAuthorizationProbeResult> ProbeAuthorizationAsync(
            TiaAuthorizationProbeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TiaAuthorizationProbeResult(Status, [], []));
        }

        public virtual void Dispose()
        {
            var marker = Environment.GetEnvironmentVariable(DisposeMarkerVariable);
            if (!string.IsNullOrWhiteSpace(marker))
            {
                File.WriteAllText(marker, "disposed");
            }
        }
    }

    public class AuthorizedProbeBinding : ProbeBindingBase
    {
        protected override TiaAuthorizationProbeStatus Status => TiaAuthorizationProbeStatus.Authorized;
    }

    public sealed class UnavailableProbeBinding : ProbeBindingBase
    {
        protected override TiaAuthorizationProbeStatus Status => TiaAuthorizationProbeStatus.Unavailable;
    }

    public sealed class DeniedProbeBinding : ProbeBindingBase
    {
        protected override TiaAuthorizationProbeStatus Status => TiaAuthorizationProbeStatus.Denied;
    }

    public sealed class TimedOutProbeBinding : ProbeBindingBase
    {
        protected override TiaAuthorizationProbeStatus Status => TiaAuthorizationProbeStatus.TimedOut;
    }

    public sealed class FailedProbeBinding : ProbeBindingBase
    {
        protected override TiaAuthorizationProbeStatus Status => TiaAuthorizationProbeStatus.Failed;
    }

    public sealed class CleanupFailureProbeBinding : AuthorizedProbeBinding
    {
        public override void Dispose()
        {
            throw new InvalidOperationException("Synthetic cleanup failure.");
        }
    }
}
