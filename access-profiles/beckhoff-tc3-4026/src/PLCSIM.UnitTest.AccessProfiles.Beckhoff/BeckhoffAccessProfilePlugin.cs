using System.Security;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Registration;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff;

public sealed class BeckhoffAccessProfilePlugin : IAccessProfilePlugin
{
    internal static readonly AccessProfileInfo ProfileInfo = new(
        "beckhoff-tc3-4026", "Beckhoff TwinCAT 3 (4026) Access Profile", "1.0.0",
        "Integrated TwinCAT 3.1 Build 4026 profile for XAE project deployment and UnitTest access to an existing local Usermode Runtime.");

    private readonly object sync = new();
    private readonly IBeckhoffEnvironmentProbe environmentProbe;
    private readonly BeckhoffProfileRegistryStore registryStore = new();
    private AccessProfileContext? context;
    private BeckhoffAccessProfileSession? session;
    private Task<AccessProfileRegistrationResult>? registrationTask;
    private Task? disposeTask;
    private bool disposed;

    public BeckhoffAccessProfilePlugin() : this(new BeckhoffEnvironmentProbe()) { }

    internal BeckhoffAccessProfilePlugin(IBeckhoffEnvironmentProbe environmentProbe) => this.environmentProbe = environmentProbe;

    public AccessProfileInfo Info => ProfileInfo;

    public void Initialize(AccessProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (this.context is not null)
                throw new InvalidOperationException("The profile is already initialized.");
            this.context = context;
        }
    }

    public Task<AccessProfileRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            if (session is not null || registrationTask is { IsCompleted: false })
                return Task.FromResult(RegistrationError("Dispose the active session or wait for registration to finish before registering again."));
            // Registry/file inspection is blocking work and must not occupy the application's UI thread.
            registrationTask = Task.Run(() => RegisterCoreAsync(context!, cancellationToken), cancellationToken);
            return registrationTask;
        }
    }

    public AccessProfileProjectSourceEvaluation EvaluateProjectSource(string projectSource)
    {
        lock (sync)
        {
            EnsureInitialized();
            if (registrationTask is { IsCompleted: false })
                return new(AccessProfileProjectSourceEvaluationState.Error, "TwinCAT registration is still running.");
            try
            {
                var registry = registryStore.Load(context!);
                if (registry.Result.State == AccessProfileRegistrationState.Error)
                    return new(AccessProfileProjectSourceEvaluationState.Error, registry.Result.Message);
                var extension = Path.GetExtension(projectSource);
                if (registry.Result.State != AccessProfileRegistrationState.Registered
                    || !registry.Result.Capabilities.ProjectSources.Any(source =>
                        source.Availability == CapabilityAvailability.Usable
                        && source.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                    return new(AccessProfileProjectSourceEvaluationState.Unsupported,
                        registry.Result.Message ?? "No registered capability supports this project source.");
                return BeckhoffProjectSourceInspector.Inspect(projectSource);
            }
            catch (Exception error) when (IsRegistrationError(error) || error is XmlException)
            {
                context!.LoggerFactory.CreateLogger<BeckhoffAccessProfilePlugin>().LogError(error, "TwinCAT project-source evaluation failed.");
                return new(AccessProfileProjectSourceEvaluationState.Error, $"TwinCAT source evaluation failed: {error.Message}");
            }
        }
    }

    public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (sync)
        {
            EnsureInitialized();
            var configuration = new BeckhoffConfigurationService(config.ProjectSource,
                ProjectOwnershipMode.ExternallyManaged, context!.TimeProvider);
            if (!MatchesIdentity(config.ProfileId, config.ProfileVersion))
                return new(false, [configuration.CreateDiagnostic("The selected profile identity/version does not match this package.")]);
            var loaded = configuration.LoadConfig(config.Values.Json);
            return new(loaded.IsSuccessful, loaded.Diagnostics);
        }
    }

    public Task<AccessProfileSessionLoadResult> LoadAsync(
        AccessProfileSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (sync)
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesIdentity(request.ProfileId, request.ProfileVersion))
                return Task.FromResult(AccessProfileSessionLoadResult.Failure("The selected profile identity/version does not match this package."));
            if (!Enum.IsDefined(request.ProjectOwnershipMode))
                return Task.FromResult(AccessProfileSessionLoadResult.Failure("The project ownership mode is invalid."));
            if (session is not null)
                return Task.FromResult(AccessProfileSessionLoadResult.Failure("Dispose the active profile session before loading another session."));
            if (registrationTask is { IsCompleted: false })
                return Task.FromResult(AccessProfileSessionLoadResult.Failure("Wait for profile registration to finish before loading a session."));

            session = new BeckhoffAccessProfileSession(context!, request, ReleaseSession);
            return Task.FromResult(AccessProfileSessionLoadResult.Success(session));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposeTask is not null) return new(disposeTask);
            disposed = true;
            var current = session;
            session = null;
            disposeTask = DisposeCoreAsync(current, registrationTask);
            return new(disposeTask);
        }
    }

    private async Task<AccessProfileRegistrationResult> RegisterCoreAsync(AccessProfileContext initializedContext, CancellationToken cancellationToken)
    {
        var logger = initializedContext.LoggerFactory.CreateLogger<BeckhoffAccessProfilePlugin>();
        var environment = BeckhoffEnvironment.Empty;
        AccessProfileRegistrationResult result;
        try
        {
            environment = environmentProbe.Inspect(cancellationToken);
            result = BeckhoffRegistration.CreateResult(environment);
        }
        catch (Exception error) when (IsRegistrationError(error))
        {
            logger.LogError(error, "TwinCAT prerequisite inspection failed.");
            result = RegistrationError($"TwinCAT prerequisite inspection failed: {error.Message}");
        }
        try
        {
            await registryStore.SaveAsync(initializedContext,
                BeckhoffProfileRegistry.Create(initializedContext, result, environment), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRegistrationError(error))
        {
            logger.LogError(error, "TwinCAT profile registry could not be written.");
            return RegistrationError($"TwinCAT profile registry could not be written: {error.Message}");
        }
        logger.LogInformation("TwinCAT profile registration finished. State={State}, Message={Message}", result.State, result.Message);
        return result;
    }

    private static async Task DisposeCoreAsync(BeckhoffAccessProfileSession? current, Task<AccessProfileRegistrationResult>? registration)
    {
        try
        {
            if (registration is not null)
            {
                try { await registration.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static AccessProfileRegistrationResult RegistrationError(string message) =>
        new(AccessProfileRegistrationState.Error, AccessProfileCapabilities.Empty, message);

    private static bool IsRegistrationError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or SecurityException or JsonException or ArgumentException or NotSupportedException;

    private void ReleaseSession()
    {
        lock (sync) session = null;
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (context is null) throw new InvalidOperationException("Initialize the profile before use.");
    }

    private static bool MatchesIdentity(string id, string? version) =>
        id == ProfileInfo.ProfileId && (version is null || version == ProfileInfo.Version);
}
