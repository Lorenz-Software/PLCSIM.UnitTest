using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Core;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

public sealed class AccessProfileSessionService : IAsyncDisposable
{
    private readonly AccessProfilePluginLoader pluginLoader;
    private readonly AccessProfileDiscoveryOptions discoveryOptions;
    private readonly CoreServiceContext coreContext;
    private readonly ILogger<AccessProfileSessionService> logger;
    private readonly object sessionSync = new();
    private Task? activeUnloadTask;

    public AccessProfileSessionService(
        AccessProfilePluginLoader pluginLoader,
        AccessProfileDiscoveryOptions discoveryOptions,
        CoreServiceContext coreContext)
    {
        this.pluginLoader = pluginLoader;
        this.discoveryOptions = discoveryOptions;
        this.coreContext = coreContext;
        logger = coreContext.LoggerFactory.CreateLogger<AccessProfileSessionService>()
            ?? NullLogger<AccessProfileSessionService>.Instance;

    }

    public AccessProfileSession? CurrentSession { get; private set; }

    public AccessProfileDiscoveryResult Discover()
    {
        return pluginLoader.Discover(discoveryOptions);
    }

    public async Task<AccessProfileSessionLoadResult> LoadAsync(
        AccessProfileSessionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        using var operationCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            timeout,
            cancellationToken);

        logger.LogInformation(
            "Loading access profile session. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
            request.ProfileId,
            request.ProfileVersion);

        try
        {
            await UnloadAsync(timeout, operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            operationCancellation.DeadlineExpired
            && ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning(
                "Access profile session replacement timed out while unloading the previous session. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, Timeout={Timeout}",
                request.ProfileId,
                request.ProfileVersion,
                timeout);
            return new AccessProfileSessionLoadResult(
                AccessProfileLoadState.TimedOut,
                null,
                new AccessProfileDiscoveryResult([], []),
                []);
        }

        var discovery = Discover();
        AccessProfileLoadResult load;
        try
        {
            load = await pluginLoader.LoadAsync(
                request,
                discovery,
                operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (operationCancellation.DeadlineExpired)
        {
            logger.LogWarning(
                "Access profile session load timed out. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}, Timeout={Timeout}",
                request.ProfileId,
                request.ProfileVersion,
                timeout);
            return new AccessProfileSessionLoadResult(
                AccessProfileLoadState.TimedOut,
                null,
                discovery,
                discovery.Diagnostics);
        }
        var diagnostics = discovery.Diagnostics.Concat(load.Diagnostics).ToArray();
        if (load.State == AccessProfileLoadState.Loaded
            && load.Profile is not null
            && load.SelectedProfile is not null)
        {
            CurrentSession = new AccessProfileSession(request, load.SelectedProfile, load.Profile);
            logger.LogInformation(
                "Access profile session loaded. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
                load.SelectedProfile.ProfileId,
                load.SelectedProfile.Version);
            return new AccessProfileSessionLoadResult(load.State, CurrentSession, discovery, diagnostics);
        }

        CurrentSession = null;
        logger.LogError(
            "Access profile session load failed. ProfileId={ProfileId}, LoadState={LoadState}",
            request.ProfileId,
            load.State);
        return new AccessProfileSessionLoadResult(load.State, null, discovery, diagnostics);
    }

    public async Task UnloadAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation = new CoreOperationCancellationScope(
            coreContext.TimeProvider,
            timeout,
            cancellationToken);

        Task unloadTask;
        lock (sessionSync)
        {
            if (activeUnloadTask is { IsCompleted: false })
            {
                unloadTask = activeUnloadTask;
            }
            else if (CurrentSession is not null)
            {
                var session = CurrentSession;
                CurrentSession = null;
                unloadTask = DisposeSessionAsync(session);
                activeUnloadTask = unloadTask;
                _ = ObserveUnloadCompletionAsync(unloadTask);
            }
            else
            {
                return;
            }
        }

        try
        {
            await unloadTask.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (operationCancellation.DeadlineExpired)
        {
            throw new TimeoutException(
                $"Access-profile unload exceeded its timeout of {timeout}.",
                ex);
        }
    }

    private async Task DisposeSessionAsync(AccessProfileSession session)
    {
        logger.LogInformation(
            "Unloading access profile session. ProfileId={ProfileId}, ProfileVersion={ProfileVersion}",
            session.SelectedProfile.ProfileId,
            session.SelectedProfile.Version);

        await session.DisposeAsync().ConfigureAwait(false);

        logger.LogInformation("Access profile session unloaded.");
    }

    private async Task ObserveUnloadCompletionAsync(Task unloadTask)
    {
        try
        {
            await unloadTask.ConfigureAwait(false);
        }
        catch
        {
            // The initiating or replacement call observes the failure.
        }
        finally
        {
            lock (sessionSync)
            {
                if (ReferenceEquals(activeUnloadTask, unloadTask))
                {
                    activeUnloadTask = null;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await UnloadAsync(
                RunConfigurationDefaults.DefaultProfileUnloadTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            logger.LogError(ex, "Access-profile unload timed out during service disposal.");
        }
    }
}
