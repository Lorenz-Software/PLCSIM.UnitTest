using System.Diagnostics;
using Microsoft.Win32;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;

internal sealed record InstalledTwinCatComponent(string Path, string ProductVersion);

internal sealed record BeckhoffEnvironment(
    string? XaeShellPath,
    IReadOnlyList<InstalledTwinCatComponent> Engineering,
    IReadOnlyList<InstalledTwinCatComponent> UsermodeRuntime)
{
    internal static BeckhoffEnvironment Empty { get; } = new(null, [], []);
}

internal interface IBeckhoffEnvironmentProbe
{
    BeckhoffEnvironment Inspect(CancellationToken cancellationToken);
}

internal sealed class BeckhoffEnvironmentProbe : IBeckhoffEnvironmentProbe
{
    private const string TwinCatKey = @"SOFTWARE\Beckhoff\TwinCAT3\3.1";
    internal const string XaeProgId = "TcXaeShell.DTE.17.0";
    private readonly Func<IReadOnlyList<string>> readInstallationRoots;
    private readonly Func<string?> readShellPath;
    private readonly Func<string, string?> readProductVersion;
    private readonly Func<string, System.Reflection.AssemblyName> readInteropIdentity;

    internal BeckhoffEnvironmentProbe()
        : this(ReadInstallationRoots, ReadShellPath, path => FileVersionInfo.GetVersionInfo(path).ProductVersion) { }

    internal BeckhoffEnvironmentProbe(Func<IReadOnlyList<string>> readInstallationRoots,
        Func<string?> readShellPath, Func<string, string?> readProductVersion,
        Func<string, System.Reflection.AssemblyName>? readInteropIdentity = null)
    {
        this.readInstallationRoots = readInstallationRoots;
        this.readShellPath = readShellPath;
        this.readProductVersion = readProductVersion;
        this.readInteropIdentity = readInteropIdentity ?? System.Reflection.AssemblyName.GetAssemblyName;
    }

    public BeckhoffEnvironment Inspect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engineering = new List<InstalledTwinCatComponent>();
        var runtime = new List<InstalledTwinCatComponent>();
        var shell = readShellPath();
        if (shell is not null && !Exists(shell, directory: false)) shell = null;
        if (shell is not null && !XaeInteropInstallation.IsAvailable(shell, readInteropIdentity)) shell = null;

        var roots = readInstallationRoots();
        if (roots.Any(path => !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("Registered TwinCAT installation paths must be absolute.");
        foreach (var root in roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var basePath = Path.Combine(root, "Components", "Base");
            if (Exists(basePath, directory: true))
            {
                foreach (var build in Directory.EnumerateDirectories(basePath, "Build_4026.*").Order(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddComponent(engineering, Path.Combine(build, "TwinCAT System Manager.x64.dll"));
                }
            }
            AddComponent(runtime, Path.Combine(root, "Runtimes", "bin", "TcSystemServiceUm.exe"));
        }
        return new(shell, engineering, runtime);
    }

    private void AddComponent(List<InstalledTwinCatComponent> components, string path)
    {
        if (!Exists(path, directory: false)) return;
        var version = readProductVersion(path);
        if (!Version.TryParse(version, out _)) throw new InvalidDataException($"Invalid TwinCAT product version in '{path}'.");
        if (IsSupportedVersion(version)) components.Add(new(path, version!));
    }

    internal static bool IsSupportedVersion(string? text) => Version.TryParse(text, out var version)
        && version.Major == 3 && version.Minor == 1 && version.Build == 4026;

    private static bool Exists(string path, bool directory)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory) == directory; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static IReadOnlyList<string> ReadInstallationRoots()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var roots = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = registry.OpenSubKey(TwinCatKey);
            if (key?.GetValue("InstallDir") is string path && !string.IsNullOrWhiteSpace(path)) roots.Add(path);
        }
        return roots;
    }

    private static string? ReadShellPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        using var progId = registry.OpenSubKey(XaeProgId + @"\CLSID");
        if (progId?.GetValue(null) is not string clsid) return null;
        if (!Guid.TryParse(clsid, out _)) throw new InvalidDataException("XAE COM registration contains an invalid CLSID.");
        using var server = registry.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32");
        return ParseServerPath(server?.GetValue("ServerExecutable") as string ?? server?.GetValue(null) as string);
    }

    internal static string? ParseServerPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        string path;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) throw new InvalidDataException("XAE COM server registration has an unterminated path.");
            path = command[1..end];
        }
        else
        {
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) throw new InvalidDataException("XAE COM server registration has no executable path.");
            path = command[..(end + 4)];
            if (end + 4 < command.Length && !char.IsWhiteSpace(command[end + 4]))
                throw new InvalidDataException("XAE COM server registration has an invalid executable path.");
        }
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("XAE COM server path must be absolute.");
        return Path.GetFullPath(path);
    }
}
