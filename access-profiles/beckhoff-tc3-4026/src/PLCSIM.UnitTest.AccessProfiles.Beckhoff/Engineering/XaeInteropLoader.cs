using System.Reflection;
using System.Runtime.Loader;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal static class XaeInteropLoader
{
    internal static void EnsureLoaded(BeckhoffEnvironment environment)
    {
        if (environment.XaeShellPath is null || !XaeInteropInstallation.IsAvailable(environment.XaeShellPath))
            throw new FileNotFoundException("Installed XAE shell and Microsoft.VisualStudio.Interop.dll are required.");
        var visualStudio = XaeInteropInstallation.GetPath(environment.XaeShellPath);
        var context = AssemblyLoadContext.GetLoadContext(typeof(XaeInteropLoader).Assembly)!;
        Load(context, visualStudio, XaeInteropInstallation.AssemblyName);
    }

    private static void Load(AssemblyLoadContext context, string path, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Installed engineering interop assembly is missing: {path}", path);
        var identity = AssemblyName.GetAssemblyName(path);
        if (identity.Name != name)
            throw new InvalidDataException($"Unexpected engineering interop assembly identity at '{path}'.");
        var loaded = context.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name);
        if (loaded is not null && loaded.GetName().FullName != identity.FullName)
            throw new InvalidDataException($"Loaded engineering interop identity differs from '{path}'.");
        if (loaded is null)
            context.LoadFromAssemblyPath(path);
    }
}
