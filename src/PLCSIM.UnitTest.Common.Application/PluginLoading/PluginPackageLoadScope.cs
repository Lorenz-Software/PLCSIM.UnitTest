using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PLCSIM.UnitTest.Common.PluginLoading;

/// <summary>
/// Loads one plugin entry assembly and its private dependencies from a bounded package directory.
/// </summary>
public sealed class PluginPackageLoadScope : IDisposable
{
    private readonly object sync = new();
    private PackageAssemblyLoadContext? loadContext;
    private bool entryPointLoadAttempted;

    public PluginPackageLoadScope(
        string packageRootPath,
        string entryAssemblyPath,
        IEnumerable<Assembly>? sharedAssemblies = null)
    {
        PackageRootPath = ResolvePackageRoot(packageRootPath);
        EntryAssemblyPath = ResolveEntryAssemblyPath(PackageRootPath, entryAssemblyPath);
        loadContext = new PackageAssemblyLoadContext(
            PackageRootPath,
            EntryAssemblyPath,
            CreateSharedAssemblyMap(sharedAssemblies));
    }

    public string PackageRootPath { get; }

    public string EntryAssemblyPath { get; }

    public TContract LoadEntryPoint<TContract>(string typeName)
        where TContract : class
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("An entry-point type name is required.", nameof(typeName));
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(loadContext is null, this);
            if (entryPointLoadAttempted)
            {
                throw new InvalidOperationException("This package scope has already attempted to load its entry point.");
            }

            entryPointLoadAttempted = true;
            var assembly = LoadEntryAssembly(loadContext);
            var entryPointType = FindEntryPointType(assembly, typeName);
            ValidateEntryPointType<TContract>(entryPointType, typeName);

            try
            {
                return Activator.CreateInstance(entryPointType) as TContract
                    ?? throw new InvalidOperationException(
                        "Entry-point activation returned no compatible instance.");
            }
            catch (Exception ex) when (ex is not PluginPackageLoadException)
            {
                throw new PluginPackageLoadException(
                    PluginPackageLoadFailureKind.EntryPointActivationFailed,
                    $"Plugin entry-point type '{typeName}' could not be activated.",
                    ex);
            }
        }
    }

    public void Dispose()
    {
        PackageAssemblyLoadContext? context;
        lock (sync)
        {
            context = loadContext;
            loadContext = null;
        }

        context?.Unload();
    }

    private Assembly LoadEntryAssembly(PackageAssemblyLoadContext context)
    {
        try
        {
            return context.LoadFromAssemblyPath(EntryAssemblyPath);
        }
        catch (Exception ex) when (ex is not PluginPackageLoadException)
        {
            throw new PluginPackageLoadException(
                PluginPackageLoadFailureKind.AssemblyLoadFailed,
                $"Plugin entry assembly '{EntryAssemblyPath}' could not be loaded.",
                ex);
        }
    }

    private Type FindEntryPointType(Assembly assembly, string typeName)
    {
        try
        {
            return assembly.GetType(typeName, throwOnError: false, ignoreCase: false)
                ?? throw new PluginPackageLoadException(
                    PluginPackageLoadFailureKind.EntryPointTypeNotFound,
                    $"Plugin entry-point type '{typeName}' was not found in '{EntryAssemblyPath}'.");
        }
        catch (PluginPackageLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PluginPackageLoadException(
                PluginPackageLoadFailureKind.AssemblyLoadFailed,
                $"Plugin entry-point type '{typeName}' could not be inspected in '{EntryAssemblyPath}'.",
                ex);
        }
    }

    private static void ValidateEntryPointType<TContract>(Type entryPointType, string typeName)
        where TContract : class
    {
        if (!typeof(TContract).IsAssignableFrom(entryPointType)
            || !entryPointType.IsClass
            || entryPointType.IsAbstract
            || !entryPointType.IsVisible)
        {
            throw new PluginPackageLoadException(
                PluginPackageLoadFailureKind.EntryPointTypeInvalid,
                $"Plugin entry-point type '{typeName}' must be a public concrete {typeof(TContract).FullName} implementation.");
        }
    }

    private static string ResolvePackageRoot(string packageRootPath)
    {
        if (string.IsNullOrWhiteSpace(packageRootPath))
        {
            throw new ArgumentException("A package root path is required.", nameof(packageRootPath));
        }

        var fullPath = Path.GetFullPath(packageRootPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Plugin package root '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static string ResolveEntryAssemblyPath(string packageRootPath, string entryAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            throw new ArgumentException("An entry assembly path is required.", nameof(entryAssemblyPath));
        }

        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(entryAssemblyPath)
                ? entryAssemblyPath
                : Path.Combine(packageRootPath, entryAssemblyPath));

        EnsurePathWithinPackage(packageRootPath, fullPath, "entry assembly");

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Plugin entry assembly '{fullPath}' does not exist.", fullPath);
        }

        return fullPath;
    }

    private static IReadOnlyDictionary<string, Assembly> CreateSharedAssemblyMap(
        IEnumerable<Assembly>? sharedAssemblies)
    {
        var assemblies = new[] { typeof(PluginPackageLoadScope).Assembly }
            .Concat(sharedAssemblies ?? Array.Empty<Assembly>());
        var result = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A shared assembly has no simple name.", nameof(sharedAssemblies));
            }

            if (result.TryGetValue(name, out var existing) && !ReferenceEquals(existing, assembly))
            {
                throw new ArgumentException(
                    $"More than one shared assembly was supplied for identity '{name}'.",
                    nameof(sharedAssemblies));
            }

            result[name] = assembly;
        }

        return result;
    }

    private static void EnsurePathWithinPackage(
        string packageRootPath,
        string candidatePath,
        string pathDescription)
    {
        if (!IsPathWithinDirectory(packageRootPath, candidatePath))
        {
            throw new ArgumentException(
                $"Plugin {pathDescription} '{candidatePath}' must remain below package root '{packageRootPath}'.");
        }
    }

    private static bool IsPathWithinDirectory(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class PackageAssemblyLoadContext : AssemblyLoadContext
    {
        private static readonly IReadOnlySet<string> PlatformAssemblyNames = GetPlatformAssemblyNames();

        private readonly string packageRootPath;
        private readonly AssemblyDependencyResolver dependencyResolver;
        private readonly IReadOnlyDictionary<string, Assembly> sharedAssemblies;

        public PackageAssemblyLoadContext(
            string packageRootPath,
            string entryAssemblyPath,
            IReadOnlyDictionary<string, Assembly> sharedAssemblies)
            : base($"PluginPackage:{Path.GetFileName(packageRootPath)}:{Guid.NewGuid():N}", isCollectible: true)
        {
            this.packageRootPath = packageRootPath;
            dependencyResolver = new AssemblyDependencyResolver(entryAssemblyPath);
            this.sharedAssemblies = sharedAssemblies;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (!string.IsNullOrWhiteSpace(assemblyName.Name)
                && sharedAssemblies.TryGetValue(assemblyName.Name, out var sharedAssembly))
            {
                if (!MatchesSharedAssemblyIdentity(assemblyName, sharedAssembly.GetName()))
                {
                    throw new FileLoadException(
                        $"Plugin dependency '{assemblyName.FullName}' conflicts with shared assembly '{sharedAssembly.FullName}'.");
                }

                return sharedAssembly;
            }

            var resolvedPath = dependencyResolver.ResolveAssemblyToPath(assemblyName);
            if (resolvedPath is null && !string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                var packageLocalPath = Path.Combine(packageRootPath, $"{assemblyName.Name}.dll");
                if (File.Exists(packageLocalPath))
                {
                    resolvedPath = packageLocalPath;
                }
            }

            if (resolvedPath is not null)
            {
                resolvedPath = Path.GetFullPath(resolvedPath);
                EnsurePathWithinPackage(packageRootPath, resolvedPath, "dependency");
                return LoadFromAssemblyPath(resolvedPath);
            }

            if (!string.IsNullOrWhiteSpace(assemblyName.Name)
                && PlatformAssemblyNames.Contains(assemblyName.Name))
            {
                return null;
            }

            throw new FileNotFoundException(
                $"Plugin dependency '{assemblyName.FullName}' was not found in package '{packageRootPath}'.");
        }

        private static bool MatchesSharedAssemblyIdentity(
            AssemblyName requestedAssembly,
            AssemblyName sharedAssembly)
        {
            return string.Equals(requestedAssembly.Name, sharedAssembly.Name, StringComparison.OrdinalIgnoreCase)
                && Equals(requestedAssembly.Version, sharedAssembly.Version)
                && string.Equals(
                    requestedAssembly.CultureName ?? string.Empty,
                    sharedAssembly.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && (requestedAssembly.GetPublicKeyToken() ?? Array.Empty<byte>())
                    .SequenceEqual(sharedAssembly.GetPublicKeyToken() ?? Array.Empty<byte>());
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var resolvedPath = dependencyResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (resolvedPath is null)
            {
                return IntPtr.Zero;
            }

            resolvedPath = Path.GetFullPath(resolvedPath);
            EnsurePathWithinPackage(packageRootPath, resolvedPath, "native dependency");
            return LoadUnmanagedDllFromPath(resolvedPath);
        }

        private static IReadOnlySet<string> GetPlatformAssemblyNames()
        {
            var trustedPlatformAssemblies =
                AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;

            if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var runtimeDirectory = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory());
            return trustedPlatformAssemblies
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(path => IsPathWithinDirectory(runtimeDirectory, Path.GetFullPath(path)))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }
    }
}
