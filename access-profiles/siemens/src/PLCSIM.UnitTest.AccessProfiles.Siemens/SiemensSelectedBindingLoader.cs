using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.PluginLoading;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensSelectedBindingLoader
{
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly SiemensBindingContext bindingContext;

    public SiemensSelectedBindingLoader(
        TimeProvider timeProvider,
        ILogger logger,
        SiemensBindingContext bindingContext)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.bindingContext = bindingContext ?? throw new ArgumentNullException(nameof(bindingContext));
    }

    public async Task<SiemensSelectedBindingLoadResult> LoadAsync(
        SiemensSelectedBindingLoadPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Diagnostic>();
        SiemensBindingPackageLease<ITiaOpennessEngineeringBinding>? tia = null;
        SiemensBindingPackageLease<IPlcsimAdvancedRuntimeBinding>? plcsimRuntime = null;

        try
        {
            tia = plan.TiaOpenness is null
                ? null
                : await LoadTargetAsync<ITiaOpennessEngineeringBinding>(
                    plan.TiaOpenness,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            plcsimRuntime = await LoadTargetAsync<IPlcsimAdvancedRuntimeBinding>(
                plan.PlcSimAdvanced,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var plcsimCommunication = plcsimRuntime?.Binding as IPlcsimUnitTestCommunicationBinding;
            if (plcsimRuntime is not null && plcsimCommunication is null)
            {
                diagnostics.Add(CreateDiagnostic(
                    "SiemensProfile.BindingLoad.ContractViolation.PlcSimCommunication",
                    "The selected PLCSIM Advanced binding does not implement the required UnitTest communication contract.",
                    $"EntryPointType={plan.PlcSimAdvanced.EntryPointTypeName}; AssemblyPath={plan.PlcSimAdvanced.EntryAssemblyPath}"));
            }

            if (diagnostics.Count > 0
                || (plan.TiaOpenness is not null && tia is null)
                || plcsimRuntime is null
                || plcsimCommunication is null)
            {
                await RollBackAcquiredBindingsAsync(
                    tia,
                    plan.TiaOpenness,
                    plcsimRuntime,
                    plan.PlcSimAdvanced,
                    diagnostics).ConfigureAwait(false);
                return CreateFailureResult(diagnostics);
            }

            var bindings = new SiemensSelectedBindings(tia, plcsimRuntime, plcsimCommunication);
            logger.LogInformation(
                "Siemens selected bindings loaded. TiaBinding={TiaBinding}, PlcSimBinding={PlcSimBindingKind}/{PlcSimBindingVersion}",
                plan.TiaOpenness is null
                    ? "not selected"
                    : $"{plan.TiaOpenness.BindingKind}/{plan.TiaOpenness.BindingVersion}",
                plan.PlcSimAdvanced.BindingKind,
                plan.PlcSimAdvanced.BindingVersion);
            return new SiemensSelectedBindingLoadResult(
                SiemensSelectedBindingLoadState.Loaded,
                bindings,
                diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackAcquiredBindingsAsync(
                tia,
                plan.TiaOpenness,
                plcsimRuntime,
                plan.PlcSimAdvanced,
                diagnostics).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(
                "SiemensProfile.BindingLoad.UnexpectedFailure",
                "Siemens selected binding acquisition failed unexpectedly.",
                ex.Message));
            await RollBackAcquiredBindingsAsync(
                tia,
                plan.TiaOpenness,
                plcsimRuntime,
                plan.PlcSimAdvanced,
                diagnostics).ConfigureAwait(false);
            return CreateFailureResult(diagnostics);
        }
    }

    private SiemensSelectedBindingLoadResult CreateFailureResult(
        IReadOnlyList<Diagnostic> diagnostics)
    {
        logger.LogError(
            "Siemens selected binding load failed. DiagnosticCount={DiagnosticCount}",
            diagnostics.Count);
        return new SiemensSelectedBindingLoadResult(
            SiemensSelectedBindingLoadState.Failed,
            null,
            diagnostics);
    }

    private async Task RollBackAcquiredBindingsAsync(
        SiemensBindingPackageLease<ITiaOpennessEngineeringBinding>? tia,
        SiemensSelectedBindingLoadTarget? tiaTarget,
        SiemensBindingPackageLease<IPlcsimAdvancedRuntimeBinding>? plcsimRuntime,
        SiemensSelectedBindingLoadTarget plcsimTarget,
        List<Diagnostic> diagnostics)
    {
        await DisposeRejectedLeaseAsync(plcsimRuntime, plcsimTarget, diagnostics).ConfigureAwait(false);
        if (tiaTarget is not null)
        {
            await DisposeRejectedLeaseAsync(tia, tiaTarget, diagnostics).ConfigureAwait(false);
        }
    }

    private async Task<SiemensBindingPackageLease<TBinding>?> LoadTargetAsync<TBinding>(
        SiemensSelectedBindingLoadTarget target,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
        where TBinding : class, ISiemensTechnologyBinding
    {
        cancellationToken.ThrowIfCancellationRequested();
        SiemensBindingPackageLease<TBinding> lease;
        try
        {
            lease = await SiemensBindingPackageLease<TBinding>.LoadAsync(
                target.PackageRootPath,
                target.EntryAssemblyPath,
                target.EntryPointTypeName,
                bindingContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginPackageLoadException ex)
        {
            diagnostics.Add(CreatePackageLoadDiagnostic(target, ex));
            return null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or BadImageFormatException
            or ArgumentException
            or NotSupportedException)
        {
            diagnostics.Add(CreateDiagnostic(
                "SiemensProfile.BindingLoad.AssemblyLoadFailed",
                "The selected Siemens binding assembly could not be loaded.",
                $"AssemblyPath={target.EntryAssemblyPath}; Error={ex.Message}"));
            return null;
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(
                "SiemensProfile.BindingLoad.EntryInitializationFailed",
                "The selected Siemens binding entry point failed during initialization.",
                $"EntryPointType={target.EntryPointTypeName}; Error={ex.Message}"));
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = lease.Binding;
            if (!string.Equals(binding.Info.BindingKind, target.BindingKind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binding.Info.BindingVersion, target.BindingVersion, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(CreateDiagnostic(
                    "SiemensProfile.BindingLoad.EntryBindingMismatch",
                    "The loaded Siemens binding identity does not match the registered selection.",
                    $"EntryPointType={target.EntryPointTypeName}; Actual={binding.Info.BindingKind}/{binding.Info.BindingVersion}; Expected={target.BindingKind}/{target.BindingVersion}"));
                await DisposeRejectedLeaseAsync(lease, target, diagnostics).ConfigureAwait(false);
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            return lease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeRejectedLeaseAsync(lease, target, diagnostics).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await DisposeRejectedLeaseAsync(lease, target, diagnostics).ConfigureAwait(false);

            diagnostics.Add(CreateDiagnostic(
                "SiemensProfile.BindingLoad.EntryInspectionFailed",
                "The selected Siemens binding entry point could not be inspected.",
                $"EntryPointType={target.EntryPointTypeName}; Error={ex.Message}"));
            return null;
        }
    }

    private Diagnostic CreatePackageLoadDiagnostic(
        SiemensSelectedBindingLoadTarget target,
        PluginPackageLoadException exception)
    {
        var code = exception.FailureKind switch
        {
            PluginPackageLoadFailureKind.EntryPointTypeNotFound => "SiemensProfile.BindingLoad.EntryTypeMissing",
            PluginPackageLoadFailureKind.EntryPointTypeInvalid => "SiemensProfile.BindingLoad.EntryTypeInvalid",
            PluginPackageLoadFailureKind.EntryPointActivationFailed => "SiemensProfile.BindingLoad.EntryTypeCreateFailed",
            _ => "SiemensProfile.BindingLoad.AssemblyLoadFailed"
        };

        return CreateDiagnostic(
            code,
            "The selected Siemens binding package could not be loaded.",
            $"PackageRoot={target.PackageRootPath}; EntryPointType={target.EntryPointTypeName}; Error={exception.InnerException?.Message ?? exception.Message}");
    }

    private async Task DisposeRejectedLeaseAsync<TBinding>(
        SiemensBindingPackageLease<TBinding>? lease,
        SiemensSelectedBindingLoadTarget target,
        List<Diagnostic> diagnostics)
        where TBinding : class, ISiemensTechnologyBinding
    {
        if (lease is null)
        {
            return;
        }

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(
                "SiemensProfile.BindingLoad.RejectedBindingDisposeFailed",
                $"The rejected Siemens binding '{target.BindingKind}/{target.BindingVersion}' could not be disposed.",
                ex.Message));
        }
    }

    private Diagnostic CreateDiagnostic(
        string code,
        string message,
        string? details = null)
    {
        return SiemensDiagnostics.ConfigurationError(
            code,
            message,
            details,
            timeProvider.GetUtcNow());
    }
}

internal sealed record SiemensSelectedBindingLoadResult(
    SiemensSelectedBindingLoadState State,
    SiemensSelectedBindings? Bindings,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => State == SiemensSelectedBindingLoadState.Loaded && Bindings is not null;
}

internal enum SiemensSelectedBindingLoadState
{
    Loaded,
    Failed
}

internal sealed class SiemensSelectedBindings : IAsyncDisposable
{
    private readonly IReadOnlyList<Func<ValueTask>> cleanups;
    private int disposed;

    public SiemensSelectedBindings(
        ITiaOpennessEngineeringBinding tiaOpenness,
        IPlcsimAdvancedRuntimeBinding plcSimAdvancedRuntime,
        IPlcsimUnitTestCommunicationBinding plcSimAdvancedCommunication)
    {
        TiaOpenness = tiaOpenness ?? throw new ArgumentNullException(nameof(tiaOpenness));
        PlcSimAdvancedRuntime = plcSimAdvancedRuntime ?? throw new ArgumentNullException(nameof(plcSimAdvancedRuntime));
        PlcSimAdvancedCommunication = plcSimAdvancedCommunication ?? throw new ArgumentNullException(nameof(plcSimAdvancedCommunication));

        var ownedBindings = new List<ISiemensTechnologyBinding> { TiaOpenness, PlcSimAdvancedRuntime };
        if (!ReferenceEquals(PlcSimAdvancedRuntime, PlcSimAdvancedCommunication))
        {
            ownedBindings.Add(PlcSimAdvancedCommunication);
        }

        cleanups = ownedBindings
            .Select(binding => new Func<ValueTask>(() => DisposeBindingAsync(binding)))
            .ToArray();
    }

    internal SiemensSelectedBindings(
        SiemensBindingPackageLease<ITiaOpennessEngineeringBinding>? tiaOpenness,
        SiemensBindingPackageLease<IPlcsimAdvancedRuntimeBinding> plcSimAdvancedRuntime,
        IPlcsimUnitTestCommunicationBinding plcSimAdvancedCommunication)
    {
        TiaOpenness = tiaOpenness?.Binding;
        PlcSimAdvancedRuntime = plcSimAdvancedRuntime.Binding;
        PlcSimAdvancedCommunication = plcSimAdvancedCommunication;
        cleanups = tiaOpenness is null
            ? [plcSimAdvancedRuntime.DisposeAsync]
            : [tiaOpenness.DisposeAsync, plcSimAdvancedRuntime.DisposeAsync];
    }

    public ITiaOpennessEngineeringBinding? TiaOpenness { get; }

    public IPlcsimAdvancedRuntimeBinding PlcSimAdvancedRuntime { get; }

    public IPlcsimUnitTestCommunicationBinding PlcSimAdvancedCommunication { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        for (var index = cleanups.Count - 1; index >= 0; index--)
        {
            try
            {
                await cleanups[index]().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException(failures);
        }
    }

    private static ValueTask DisposeBindingAsync(ISiemensTechnologyBinding binding)
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
