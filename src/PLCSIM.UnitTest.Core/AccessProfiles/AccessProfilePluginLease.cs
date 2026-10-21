using System.Runtime.ExceptionServices;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.PluginLoading;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.Core.AccessProfiles;

internal sealed class AccessProfilePluginLease : IAsyncDisposable
{
    private IAccessProfilePlugin? plugin;
    private PluginPackageLoadScope? packageScope;
    private readonly bool ownsPlugin;
    private int disposed;

    public AccessProfilePluginLease(
        IAccessProfilePlugin plugin,
        PluginPackageLoadScope? packageScope,
        bool ownsPlugin)
    {
        this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        this.packageScope = packageScope;
        this.ownsPlugin = ownsPlugin;
    }

    public IAccessProfilePlugin Plugin => plugin
        ?? throw new ObjectDisposedException(nameof(AccessProfilePluginLease));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var ownedPlugin = plugin;
        plugin = null;
        var ownedPackageScope = packageScope;
        packageScope = null;

        Exception? pluginFailure = null;
        if (ownsPlugin && ownedPlugin is not null)
        {
            try
            {
                await ownedPlugin.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pluginFailure = ex;
            }
        }

        Exception? scopeFailure = null;
        try
        {
            ownedPackageScope?.Dispose();
        }
        catch (Exception ex)
        {
            scopeFailure = ex;
        }

        ThrowDisposeFailure(pluginFailure, scopeFailure);
    }

    internal static void ThrowDisposeFailure(Exception? first, Exception? second)
    {
        if (first is not null && second is not null)
        {
            throw new AggregateException(first, second);
        }

        var failure = first ?? second;
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

internal sealed class PackageOwnedAccessProfileSession : IAccessProfileSession
{
    private IAccessProfileSession? session;
    private AccessProfilePluginLease? pluginLease;
    private int disposed;

    public PackageOwnedAccessProfileSession(
        IAccessProfileSession session,
        AccessProfilePluginLease pluginLease)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.pluginLease = pluginLease ?? throw new ArgumentNullException(nameof(pluginLease));
    }

    public AccessProfileInfo Info => GetSession().Info;

    public event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler
    {
        add => GetSession().DiagnosticEventHandler += value;
        remove => GetSession().DiagnosticEventHandler -= value;
    }

    public event EventHandler<ProfileStateChangedEventArgs> StateChangedEventHandler
    {
        add => GetSession().StateChangedEventHandler += value;
        remove => GetSession().StateChangedEventHandler -= value;
    }

    public event EventHandler<AccessHealthChangedEventArgs> HealthChangedEventHandler
    {
        add => GetSession().HealthChangedEventHandler += value;
        remove => GetSession().HealthChangedEventHandler -= value;
    }

    public AccessHealth Health => GetSession().Health;

    public IAccessProfileConfigurationService Configuration => GetSession().Configuration
        ?? throw new InvalidOperationException("The loaded access profile has no configuration service.");

    public IPlcProjectService Project => GetSession().Project;

    public IPlcControlService PlcControl => GetSession().PlcControl;

    public IUnitTestLifecycleService UnitTestLifecycle => GetSession().UnitTestLifecycle;

    public IUnitTestEventStreamService UnitTestEventStream => GetSession().UnitTestEventStream;

    public Task<OperationResult<PreparedPlcRuntime>> ReconnectRuntimeAsync(
        PlcRuntimeReconnectRequest request,
        CancellationToken cancellationToken)
    {
        return GetSession().ReconnectRuntimeAsync(request, cancellationToken);
    }

    public Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(
        CommunicationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        return GetSession().PrepareUnitTestCommunicationAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var ownedSession = session;
        var ownedPluginLease = pluginLease;

        Exception? sessionFailure = null;
        try
        {
            if (ownedSession is not null)
            {
                await ownedSession.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            sessionFailure = ex;
        }
        finally
        {
            session = null;
        }

        Exception? pluginFailure = null;
        try
        {
            if (ownedPluginLease is not null)
            {
                await ownedPluginLease.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            pluginFailure = ex;
        }
        finally
        {
            pluginLease = null;
        }

        AccessProfilePluginLease.ThrowDisposeFailure(sessionFailure, pluginFailure);
    }

    private IAccessProfileSession GetSession()
    {
        return session ?? throw new ObjectDisposedException(nameof(PackageOwnedAccessProfileSession));
    }
}
