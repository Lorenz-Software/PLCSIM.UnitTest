namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;

internal static class XaeInteropInstallation
{
    internal const string AssemblyName = "Microsoft.VisualStudio.Interop";

    internal static string GetPath(string shellPath) => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(shellPath) ?? throw new InvalidDataException("Installed XAE shell has no directory."),
        "PublicAssemblies", AssemblyName + ".dll"));

    internal static bool IsAvailable(string shellPath, Func<string, System.Reflection.AssemblyName>? readIdentity = null)
    {
        var path = GetPath(shellPath);
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory)) return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        var identity = (readIdentity ?? System.Reflection.AssemblyName.GetAssemblyName)(path);
        if (identity.Name != AssemblyName)
            throw new InvalidDataException($"Unexpected engineering interop assembly identity at '{path}'.");
        return true;
    }
}
