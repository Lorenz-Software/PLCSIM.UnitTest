using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.PluginLoading;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Runtime.ExceptionServices;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensBindingPackageLease<TBinding> : IAsyncDisposable
    where TBinding : class, ISiemensTechnologyBinding
{
    private readonly PluginPackageLoadScope packageScope;
    private TBinding? binding;
    private int disposed;

    private SiemensBindingPackageLease(
        TBinding binding,
        PluginPackageLoadScope packageScope)
    {
        this.binding = binding;
        this.packageScope = packageScope;
    }

    public TBinding Binding => binding
        ?? throw new ObjectDisposedException(nameof(SiemensBindingPackageLease<TBinding>));

    public static async Task<SiemensBindingPackageLease<TBinding>> LoadAsync(
        string packageRootPath,
        string entryAssemblyPath,
        string entryPointTypeName,
        SiemensBindingContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PluginPackageLoadScope? packageScope = null;
        TBinding? binding = null;
        try
        {
            packageScope = new PluginPackageLoadScope(
                packageRootPath,
                entryAssemblyPath,
                [
                    typeof(ISiemensTechnologyBinding).Assembly,
                    typeof(UnitTestContractVersions).Assembly,
                    typeof(Diagnostic).Assembly,
                    typeof(ILoggerFactory).Assembly
                ]);
            binding = packageScope.LoadEntryPoint<TBinding>(entryPointTypeName);
            cancellationToken.ThrowIfCancellationRequested();
            binding.Initialize(context ?? throw new ArgumentNullException(nameof(context)));
            cancellationToken.ThrowIfCancellationRequested();
            return new SiemensBindingPackageLease<TBinding>(binding, packageScope);
        }
        catch (Exception loadFailure)
        {
            var cleanupFailures = new List<Exception>();
            if (binding is not null)
            {
                try
                {
                    await DisposeBindingAsync(binding).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }

            if (packageScope is not null)
            {
                try
                {
                    packageScope.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException([loadFailure, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(loadFailure).Throw();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var bindingToDispose = Interlocked.Exchange(ref binding, null);

        Exception? bindingFailure = null;
        try
        {
            if (bindingToDispose is not null)
            {
                await DisposeBindingAsync(bindingToDispose).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            bindingFailure = ex;
        }

        Exception? scopeFailure = null;
        try
        {
            packageScope.Dispose();
        }
        catch (Exception ex)
        {
            scopeFailure = ex;
        }

        if (bindingFailure is not null && scopeFailure is not null)
        {
            throw new AggregateException(bindingFailure, scopeFailure);
        }

        var failure = bindingFailure ?? scopeFailure;
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static ValueTask DisposeBindingAsync(TBinding binding)
    {
        if (binding is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        if (binding is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
