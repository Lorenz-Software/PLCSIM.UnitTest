using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensProfileResourceScope : IAsyncDisposable
{
    private readonly List<SiemensProfileResourceCleanup> cleanups = new();
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private bool isDisposed;
    private bool ownershipReleased;

    public SiemensProfileResourceScope(
        TimeProvider timeProvider,
        ILogger logger)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void OwnSelectedBindings(SiemensSelectedBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        OwnOptionalDisposable("Siemens selected bindings", bindings);
    }

    public void OwnTiaSession(ITiaEngineeringSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        AddCleanup(
            "TIA engineering session",
            cancellationToken => new ValueTask(session.CloseAsync(cancellationToken)));
    }

    public void OwnPlcsimRuntimeSession(IPlcsimRuntimeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        AddCleanup(
            "PLCSIM runtime session",
            cancellationToken => new ValueTask(session.CloseAsync(cancellationToken)));
    }

    public void OwnOptionalDisposable(
        string resourceName,
        object resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(resource);

        if (resource is IAsyncDisposable asyncDisposable)
        {
            AddCleanup(resourceName, _ => asyncDisposable.DisposeAsync());
            return;
        }

        if (resource is IDisposable disposable)
        {
            AddCleanup(
                resourceName,
                _ =>
                {
                    disposable.Dispose();
                    return ValueTask.CompletedTask;
                });
        }
    }

    public void ReleaseOwnership()
    {
        ThrowIfDisposed();
        ownershipReleased = true;
        cleanups.Clear();
    }

    public async ValueTask<SiemensProfileResourceCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (isDisposed || ownershipReleased || cleanups.Count == 0)
        {
            isDisposed = true;
            return new SiemensProfileResourceCleanupResult(Array.Empty<Diagnostic>());
        }

        isDisposed = true;
        var diagnostics = new List<Diagnostic>();
        for (var index = cleanups.Count - 1; index >= 0; index--)
        {
            var cleanup = cleanups[index];
            try
            {
                await cleanup.Cleanup(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                diagnostics.Add(CreateCleanupDiagnostic(cleanup.ResourceName, ex));
                logger.LogError(
                    ex,
                    "Siemens profile resource cleanup failed. ResourceName={ResourceName}",
                    cleanup.ResourceName);
            }
        }

        cleanups.Clear();
        return new SiemensProfileResourceCleanupResult(diagnostics);
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void AddCleanup(
        string resourceName,
        Func<CancellationToken, ValueTask> cleanup)
    {
        ThrowIfDisposed();
        cleanups.Add(new SiemensProfileResourceCleanup(resourceName, cleanup));
    }

    private Diagnostic CreateCleanupDiagnostic(
        string resourceName,
        Exception ex)
    {
        return SiemensDiagnostics.ConfigurationError(
            "SiemensProfile.ResourceCleanupFailed",
            $"Siemens profile resource cleanup failed for '{resourceName}'.",
            ex.Message,
            timeProvider.GetUtcNow());
    }

    private void ThrowIfDisposed()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(SiemensProfileResourceScope));
        }
    }
}

internal sealed record SiemensProfileResourceCleanupResult(IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => Diagnostics.Count == 0;
}

internal sealed record SiemensProfileResourceCleanup(
    string ResourceName,
    Func<CancellationToken, ValueTask> Cleanup);
