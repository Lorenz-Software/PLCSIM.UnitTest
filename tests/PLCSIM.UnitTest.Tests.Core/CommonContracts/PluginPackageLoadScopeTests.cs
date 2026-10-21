using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.PluginLoading;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class PluginPackageLoadScopeTests
{
    [Fact]
    public void ConstructorRejectsEntryAssemblyOutsidePackage()
    {
        var packageRoot = CreateTemporaryDirectory();

        try
        {
            var exception = Assert.Throws<ArgumentException>(
                () => new PluginPackageLoadScope(
                    packageRoot,
                    typeof(PluginPackageLoadScopeTests).Assembly.Location));

            Assert.Contains("must remain below package root", exception.Message);
        }
        finally
        {
            DeleteTemporaryDirectory(packageRoot);
        }
    }

    [Fact]
    public void ConstructorRejectsMissingEntryAssembly()
    {
        var packageRoot = CreateTemporaryDirectory();

        try
        {
            Assert.Throws<FileNotFoundException>(
                () => new PluginPackageLoadScope(packageRoot, "missing.dll"));
        }
        finally
        {
            DeleteTemporaryDirectory(packageRoot);
        }
    }

    [Fact]
    public void LoadEntryPointUsesPackageContextAndSharedCommonApplicationContract()
    {
        var packageRoot = CreatePackageWithTestAssembly();

        try
        {
            VerifyEntryPointLoad(packageRoot);
        }
        finally
        {
            DeleteTemporaryDirectory(packageRoot);
        }
    }

    [Fact]
    public void LoadEntryPointClassifiesMissingAndInvalidTypes()
    {
        var missingTypePackage = CreatePackageWithTestAssembly();
        var invalidTypePackage = CreatePackageWithTestAssembly();

        try
        {
            using var missingTypeScope = new PluginPackageLoadScope(
                missingTypePackage,
                Path.GetFileName(typeof(PluginPackageLoadScopeTests).Assembly.Location));

            var missingType = Assert.Throws<PluginPackageLoadException>(
                () => missingTypeScope.LoadEntryPoint<IArtifactExporter>("Missing.EntryPoint"));
            Assert.Equal(
                PluginPackageLoadFailureKind.EntryPointTypeNotFound,
                missingType.FailureKind);

            using var invalidTypeScope = new PluginPackageLoadScope(
                invalidTypePackage,
                Path.GetFileName(typeof(PluginPackageLoadScopeTests).Assembly.Location));

            var invalidType = Assert.Throws<PluginPackageLoadException>(
                () => invalidTypeScope.LoadEntryPoint<IAsyncDisposable>(
                    typeof(PluginPackageLoadScopeTestEntryPoint).FullName!));
            Assert.Equal(
                PluginPackageLoadFailureKind.EntryPointTypeInvalid,
                invalidType.FailureKind);
        }
        finally
        {
            DeleteTemporaryDirectory(missingTypePackage);
            DeleteTemporaryDirectory(invalidTypePackage);
        }
    }

    [Fact]
    public void LoadEntryPointResolvesPrivateDependencyFromPackage()
    {
        var packageRoot = CreatePackageWithPrivateDependency(includeDependency: true);

        try
        {
            var dependencyLocation = LoadPrivateDependencyEntryPoint(packageRoot);

            Assert.StartsWith(packageRoot, dependencyLocation, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(typeof(Assert).Assembly.Location, dependencyLocation);
        }
        finally
        {
            DeleteTemporaryDirectory(packageRoot);
        }
    }

    [Fact]
    public void LoadEntryPointClassifiesMissingPrivateDependency()
    {
        var packageRoot = CreatePackageWithPrivateDependency(includeDependency: false);

        try
        {
            var failure = LoadPrivateDependencyEntryPointFailure(packageRoot);

            Assert.Equal(
                PluginPackageLoadFailureKind.EntryPointActivationFailed,
                failure.FailureKind);
            Assert.True(failure.HasExpectedInnerException);
        }
        finally
        {
            DeleteTemporaryDirectory(packageRoot);
        }
    }

    [Fact]
    public void LoadEntryPointRejectsConflictingSharedDependencyVersion()
    {
        var packageRoot = CreatePackageWithPrivateDependency(includeDependency: true);
        var dependencyName = typeof(Assert).Assembly.GetName();
        var conflictingName = new AssemblyName(dependencyName.Name!)
        {
            Version = new Version((dependencyName.Version?.Major ?? 0) + 1, 0, 0, 0),
            CultureInfo = dependencyName.CultureInfo
        };
        var conflictingAssembly = AssemblyBuilder.DefineDynamicAssembly(
            conflictingName,
            AssemblyBuilderAccess.Run);

        try
        {
            var failure = LoadPrivateDependencyEntryPointFailure(
                packageRoot,
                conflictingAssembly);

            Assert.Equal(
                PluginPackageLoadFailureKind.EntryPointActivationFailed,
                failure.FailureKind);
            Assert.True(failure.HasExpectedInnerException);
        }
        finally
        {
            DeleteTemporaryDirectory(packageRoot);
        }
    }

    [Fact]
    public void DisposeUnloadsCollectibleContextAndReleasesPackageFiles()
    {
        var packageRoot = CreatePackageWithTestAssembly();

        var loadContext = LoadAndDisposePackage(packageRoot);
        WaitForUnload(loadContext);

        Assert.False(loadContext.IsAlive);
        Directory.Delete(packageRoot, recursive: true);
    }

    [Fact]
    public void RepeatedPackageScopesUnloadIndependently()
    {
        var packageRoot = CreatePackageWithTestAssembly();
        var contexts = Enumerable.Range(0, 5)
            .Select(_ => LoadAndDisposePackage(packageRoot))
            .ToArray();

        foreach (var context in contexts)
        {
            WaitForUnload(context);
            Assert.False(context.IsAlive);
        }

        Directory.Delete(packageRoot, recursive: true);
    }

    [Fact]
    public void ScopeAllowsOneLoadAttemptAndRejectsUseAfterDispose()
    {
        var packageRoot = CreatePackageWithTestAssembly();

        try
        {
            VerifySingleLoadAndDispose(packageRoot);
        }
        finally
        {
            DeleteTemporaryDirectory(packageRoot);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyEntryPointLoad(string packageRoot)
    {
        using var scope = new PluginPackageLoadScope(
            packageRoot,
            Path.GetFileName(typeof(PluginPackageLoadScopeTests).Assembly.Location));

        var entryPoint = scope.LoadEntryPoint<IArtifactExporter>(
            typeof(PluginPackageLoadScopeTestEntryPoint).FullName!);

        Assert.Equal(ArtifactFormat.JUnitXml, entryPoint.Format);
        Assert.NotSame(
            AssemblyLoadContext.Default,
            AssemblyLoadContext.GetLoadContext(entryPoint.GetType().Assembly));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifySingleLoadAndDispose(string packageRoot)
    {
        var scope = new PluginPackageLoadScope(
            packageRoot,
            Path.GetFileName(typeof(PluginPackageLoadScopeTests).Assembly.Location));

        _ = scope.LoadEntryPoint<IArtifactExporter>(
            typeof(PluginPackageLoadScopeTestEntryPoint).FullName!);

        Assert.Throws<InvalidOperationException>(
            () => scope.LoadEntryPoint<IArtifactExporter>(
                typeof(PluginPackageLoadScopeTestEntryPoint).FullName!));

        scope.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => scope.LoadEntryPoint<IArtifactExporter>(
                typeof(PluginPackageLoadScopeTestEntryPoint).FullName!));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LoadPrivateDependencyEntryPoint(string packageRoot)
    {
        using var scope = new PluginPackageLoadScope(
            packageRoot,
            Path.GetFileName(typeof(PluginPackageLoadScopeTests).Assembly.Location));
        var entryPoint = scope.LoadEntryPoint<IArtifactExporter>(
            typeof(PluginPackageLoadScopePrivateDependencyEntryPoint).FullName!);

        return Assert.IsType<string>(
            entryPoint.GetType()
                .GetProperty(nameof(PluginPackageLoadScopePrivateDependencyEntryPoint.PrivateDependencyAssemblyLocation))!
                .GetValue(entryPoint));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PackageLoadFailure LoadPrivateDependencyEntryPointFailure(
        string packageRoot,
        Assembly? sharedAssembly = null)
    {
        using var scope = new PluginPackageLoadScope(
            packageRoot,
            Path.GetFileName(typeof(PluginPackageLoadScopeTests).Assembly.Location),
            sharedAssembly is null ? null : [sharedAssembly]);

        var exception = Assert.Throws<PluginPackageLoadException>(
            () => scope.LoadEntryPoint<IArtifactExporter>(
                typeof(PluginPackageLoadScopePrivateDependencyEntryPoint).FullName!));
        var expectedInnerType = sharedAssembly is null
            ? typeof(FileNotFoundException)
            : typeof(FileLoadException);

        return new PackageLoadFailure(
            exception.FailureKind,
            FindInnerException(exception, expectedInnerType) is not null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndDisposePackage(string packageRoot)
    {
        var scope = new PluginPackageLoadScope(
            packageRoot,
            Path.GetFileName(typeof(PluginPackageLoadScopeTests).Assembly.Location));
        var entryPoint = scope.LoadEntryPoint<IArtifactExporter>(
            typeof(PluginPackageLoadScopeTestEntryPoint).FullName!);
        var context = AssemblyLoadContext.GetLoadContext(entryPoint.GetType().Assembly);
        Assert.NotNull(context);
        var contextReference = new WeakReference(context);

        scope.Dispose();
        return contextReference;
    }

    private static void WaitForUnload(WeakReference contextReference)
    {
        for (var attempt = 0; contextReference.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
    }

    private static Exception? FindInnerException(
        Exception exception,
        Type exceptionType)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (exceptionType.IsInstanceOfType(current))
            {
                return current;
            }
        }

        return null;
    }

    private static string CreatePackageWithPrivateDependency(bool includeDependency)
    {
        var packageRoot = CreatePackageWithTestAssembly();
        if (includeDependency)
        {
            var dependencyPath = typeof(Assert).Assembly.Location;
            File.Copy(
                dependencyPath,
                Path.Combine(packageRoot, Path.GetFileName(dependencyPath)));
        }

        return packageRoot;
    }

    private static string CreatePackageWithTestAssembly()
    {
        var packageRoot = CreateTemporaryDirectory();
        var sourcePath = typeof(PluginPackageLoadScopeTests).Assembly.Location;
        File.Copy(sourcePath, Path.Combine(packageRoot, Path.GetFileName(sourcePath)));
        return packageRoot;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "plcsim-unittest-plugin-loading",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
            }
        }
    }

    private sealed record PackageLoadFailure(
        PluginPackageLoadFailureKind FailureKind,
        bool HasExpectedInnerException);
}

public class PluginPackageLoadScopeTestEntryPoint : IArtifactExporter
{
    public ArtifactFormat Format => ArtifactFormat.JUnitXml;

    public event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler
    {
        add { }
        remove { }
    }

    public Task<ExportResult> ExportAsync(
        ExportRequest request,
        UnitTestRunEpochSnapshot runEpoch,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(
            new ExportResult(request.Format, ArtifactState.Written, request.OutputPath));
    }
}

public sealed class PluginPackageLoadScopePrivateDependencyEntryPoint
    : PluginPackageLoadScopeTestEntryPoint
{
    public PluginPackageLoadScopePrivateDependencyEntryPoint()
    {
        PrivateDependencyAssemblyLocation = typeof(Assert).Assembly.Location;
    }

    public string PrivateDependencyAssemblyLocation { get; }
}
