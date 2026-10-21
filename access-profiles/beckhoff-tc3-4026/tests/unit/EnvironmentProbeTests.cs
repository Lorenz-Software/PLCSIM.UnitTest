using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EnvironmentProbeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "beckhoff-environment-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProbeUsesRegisteredRootProductVersionsAndDoesNotSelectASinglePatch()
    {
        var shell = CreateFile("Shell/TcXaeShell.exe");
        CreateFile("Shell/PublicAssemblies/Microsoft.VisualStudio.Interop.dll");
        var older = CreateFile("Components/Base/Build_4026.26/TwinCAT System Manager.x64.dll");
        var newer = CreateFile("Components/Base/Build_4026.30/TwinCAT System Manager.x64.dll");
        _ = CreateFile("Components/Base/Build_4024.78/TwinCAT System Manager.x64.dll");
        var runtime = CreateFile("Runtimes/bin/TcSystemServiceUm.exe");
        var versions = new Dictionary<string, string> { [older] = "3.1.4026.26", [newer] = "3.1.4026.30", [runtime] = "3.1.4026.26" };
        var probe = new BeckhoffEnvironmentProbe(() => [root, root], () => shell, path => versions[path],
            _ => new System.Reflection.AssemblyName(XaeInteropInstallation.AssemblyName));
        var environment = probe.Inspect(default);
        Assert.Equal(shell, environment.XaeShellPath);
        Assert.Equal(2, environment.Engineering.Count);
        Assert.Single(environment.UsermodeRuntime);
        Assert.Equal(new[] { "3.1.4026.26", "3.1.4026.30" }, environment.Engineering.Select(item => item.ProductVersion));
        Assert.Equal(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileRegistrationState.Registered,
            BeckhoffRegistration.CreateResult(environment).State);
    }

    [Fact]
    public void ShellWithoutInstalledVisualStudioInteropCannotAdvertiseExecution()
    {
        var shell = CreateFile("Shell/TcXaeShell.exe");
        CreateFile("Components/Base/Build_4026.27/TwinCAT System Manager.x64.dll");
        CreateFile("Runtimes/bin/TcSystemServiceUm.exe");
        var probe = new BeckhoffEnvironmentProbe(() => [root], () => shell, _ => "3.1.4026.27");
        var environment = probe.Inspect(default);
        Assert.Null(environment.XaeShellPath);
        Assert.Single(environment.Engineering);
        Assert.Single(environment.UsermodeRuntime);
        Assert.Equal(PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileRegistrationState.Unavailable,
            BeckhoffRegistration.CreateResult(environment).State);
        Assert.Throws<FileNotFoundException>(() =>
            PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering.XaeInteropLoader.EnsureLoaded(environment));
    }

    [Fact]
    public void InteropIdentityIsCheckedWithoutLoadingOrActivatingTheVendor()
    {
        var shell = CreateFile("Shell/TcXaeShell.exe");
        var interop = CreateFile("Shell/PublicAssemblies/Microsoft.VisualStudio.Interop.dll");
        Assert.Equal(interop, XaeInteropInstallation.GetPath(shell));
        Assert.Throws<BadImageFormatException>(() => XaeInteropInstallation.IsAvailable(shell));
        Assert.Throws<BadImageFormatException>(() =>
            PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering.XaeInteropLoader.EnsureLoaded(new(shell, [], [])));
        Assert.Throws<InvalidDataException>(() => new BeckhoffEnvironmentProbe(() => [root], () => shell, _ => "3.1.4026.27",
            _ => new System.Reflection.AssemblyName("WrongAssembly")).Inspect(default));
        Assert.Throws<UnauthorizedAccessException>(() => new BeckhoffEnvironmentProbe(() => [root], () => shell, _ => "3.1.4026.27",
            _ => throw new UnauthorizedAccessException()).Inspect(default));
        var environment = new BeckhoffEnvironmentProbe(() => [root], () => shell, _ => "3.1.4026.27",
            _ => new System.Reflection.AssemblyName(XaeInteropInstallation.AssemblyName)).Inspect(default);
        Assert.Equal(shell, environment.XaeShellPath);
        Assert.False(File.Exists(Path.Combine(root, "Components", "TcBlockDiagram", "Common", "TCatSysManagerLib.dll")));
    }

    [Fact]
    public void MissingFilesAreUnavailableButUnreadableVersionMetadataIsAnError()
    {
        var probe = new BeckhoffEnvironmentProbe(() => [root], () => Path.Combine(root, "missing.exe"), _ => throw new InvalidOperationException());
        var missing = probe.Inspect(default);
        Assert.Null(missing.XaeShellPath);
        Assert.Empty(missing.Engineering);
        Assert.Empty(missing.UsermodeRuntime);
        CreateFile("Runtimes/bin/TcSystemServiceUm.exe");
        Assert.Throws<UnauthorizedAccessException>(() => new BeckhoffEnvironmentProbe(() => [root], () => null,
            _ => throw new UnauthorizedAccessException()).Inspect(default));
        Assert.Throws<InvalidDataException>(() => new BeckhoffEnvironmentProbe(() => [root], () => null, _ => null).Inspect(default));
    }

    [Fact]
    public void WrongFamilyIsNotPublishedAndInspectionHonorsCancellation()
    {
        CreateFile("Runtimes/bin/TcSystemServiceUm.exe");
        var probe = new BeckhoffEnvironmentProbe(() => [root], () => null, _ => "3.1.4024.78");
        Assert.Empty(probe.Inspect(default).UsermodeRuntime);
        Assert.ThrowsAny<OperationCanceledException>(() => probe.Inspect(new(true)));
    }

    [Fact]
    public void ShellRegistrationParsingDoesNotExecuteItsCommand()
    {
        var executable = Path.Combine(root, "Path With Spaces", "TcXaeShell.exe");
        Assert.Equal(executable, BeckhoffEnvironmentProbe.ParseServerPath('"' + executable + "\" /Embedding"));
        Assert.Equal(executable, BeckhoffEnvironmentProbe.ParseServerPath(executable + " /Embedding"));
        Assert.Null(BeckhoffEnvironmentProbe.ParseServerPath(null));
        Assert.Throws<InvalidDataException>(() => BeckhoffEnvironmentProbe.ParseServerPath("TcXaeShell.exe"));
        Assert.Throws<InvalidDataException>(() => BeckhoffEnvironmentProbe.ParseServerPath("\"unterminated"));
        Assert.Throws<InvalidDataException>(() => BeckhoffEnvironmentProbe.ParseServerPath(executable + ".invalid"));
    }

    private string CreateFile(string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test fixture");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
