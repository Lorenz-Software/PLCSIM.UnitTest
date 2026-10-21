using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.Common.AccessProfiles;

/// <summary>
/// Factory contract implemented by an access profile plugin.
/// </summary>
public interface IAccessProfilePlugin : IAsyncDisposable
{
    /// <summary>
    /// Gets stable profile metadata used for discovery, selection, and diagnostics.
    /// </summary>
    AccessProfileInfo Info { get; }

    /// <summary>
    /// Binds the plugin instance to its application and profile-root context.
    /// The loader calls this exactly once before using any other plugin method.
    /// </summary>
    void Initialize(AccessProfileContext context);

    /// <summary>
    /// Registers or refreshes the profile-owned runtime registry.
    /// Implementations own synchronization of vendor-specific resources.
    /// </summary>
    Task<AccessProfileRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new AccessProfileRegistrationResult(
            AccessProfileRegistrationState.Error,
            AccessProfileCapabilities.Empty,
            "The access profile does not implement registration."));
    }

    /// <summary>
    /// Validates profile-specific configuration without creating a loaded profile instance.
    /// </summary>
    AccessProfileValidationResult ValidateConfig(AccessProfileConfig config);

    /// <summary>
    /// Loads a stateful access profile session from setup-only application intent.
    /// </summary>
    Task<AccessProfileSessionLoadResult> LoadAsync(
        AccessProfileSessionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates whether this profile can handle a project source from its
    /// profile-owned registration state without creating a loaded profile.
    /// </summary>
    AccessProfileProjectSourceEvaluation EvaluateProjectSource(string projectSource)
    {
        return new AccessProfileProjectSourceEvaluation(
            AccessProfileProjectSourceEvaluationState.Unsupported,
            "The access profile does not implement project source evaluation.");
    }
}

/// <summary>
/// Loaded access profile session that owns stateful access lifecycle and exposes the required functional services.
/// </summary>
public interface IAccessProfileSession : IAsyncDisposable
{
    /// <summary>
    /// Gets metadata of the loaded access profile.
    /// </summary>
    AccessProfileInfo Info { get; }

    /// <summary>
    /// Publishes profile-level diagnostics.
    /// </summary>
    event EventHandler<DiagnosticEventArgs> DiagnosticEventHandler;

    /// <summary>
    /// Publishes profile-level state changes such as ready, invalidated, or disposed.
    /// </summary>
    event EventHandler<ProfileStateChangedEventArgs> StateChangedEventHandler;

    /// <summary>
    /// Publishes health changes for the profile and its functional services.
    /// </summary>
    event EventHandler<AccessHealthChangedEventArgs> HealthChangedEventHandler;

    /// <summary>
    /// Gets the current application-facing health of the loaded profile.
    /// </summary>
    AccessHealth Health { get; }

    /// <summary>
    /// Gets the side-effect-free profile configuration boundary.
    /// Profiles without user-configurable values expose an empty field projection.
    /// </summary>
    IAccessProfileConfigurationService Configuration { get; }

    /// <summary>
    /// Gets the PLC project preparation and transfer service.
    /// </summary>
    IPlcProjectService Project { get; }

    /// <summary>
    /// Gets the PLC runtime provisioning and control service.
    /// </summary>
    IPlcControlService PlcControl { get; }

    /// <summary>
    /// Gets the UnitTest RunControl lifecycle service.
    /// </summary>
    IUnitTestLifecycleService UnitTestLifecycle { get; }

    /// <summary>
    /// Gets the UnitTest event-stream communication service.
    /// </summary>
    IUnitTestEventStreamService UnitTestEventStream { get; }

    /// <summary>
    /// Replaces profile-owned runtime and UnitTest communication resources for an already prepared target.
    /// Cancellation may be accepted only before the profile detaches the existing runtime boundary.
    /// </summary>
    Task<OperationResult<PreparedPlcRuntime>> ReconnectRuntimeAsync(
        PlcRuntimeReconnectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OperationResult<PreparedPlcRuntime>.Failure(
            "The access profile does not implement runtime reconnect."));
    }

    /// <summary>
    /// Validates and prepares UnitTest communication after the PLC runtime is running.
    /// </summary>
    Task<OperationResult<CommunicationSession>> PrepareUnitTestCommunicationAsync(
        CommunicationPrepareRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Stable self-information of an access profile.
/// </summary>
public sealed record AccessProfileInfo(
    string ProfileId,
    string DisplayName,
    string Version,
    string Description);

/// <summary>
/// Application-level profile selection plus opaque profile-specific values.
/// </summary>
public sealed record AccessProfileConfig(
    string ProfileId,
    string? ProfileVersion,
    AccessProfileConfigurationPayload Values,
    string ProjectSource);

/// <summary>
/// Application-owned intent for one stateful access-profile session.
/// </summary>
public sealed record AccessProfileSessionRequest(
    string ProfileId,
    string? ProfileVersion,
    string ProjectSource,
    ProjectOwnershipMode ProjectOwnershipMode);

public enum ProjectOwnershipMode
{
    ApplicationManaged,
    ExternallyManaged
}

public enum RuntimeProvisioningMode
{
    UseExisting = 0,
    CreateOwned = 1
}

/// <summary>
/// Defines the application-owned intent for releasing a prepared runtime resource.
/// </summary>
public enum RuntimeResourceReleaseMode
{
    KeepResource,
    DestroyResource
}

/// <summary>
/// Opaque raw JSON payload handed from application configuration to the selected access profile.
/// </summary>
public sealed record AccessProfileConfigurationPayload(string Json)
{
    public static AccessProfileConfigurationPayload Empty { get; } = new("{}");
}

/// <summary>
/// Stable host, selected package, and writable profile-data information bound to one plugin instance.
/// </summary>
public sealed record AccessProfileContext(
    Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory,
    TimeProvider TimeProvider,
    string ProfileRootPath,
    string ProfileDataDirectory,
    OperationalLoggingOptions Logging);

/// <summary>
/// Lightweight project-source selection result returned by an access profile.
/// </summary>
public sealed record AccessProfileProjectSourceEvaluation(
    AccessProfileProjectSourceEvaluationState State,
    string? Message);

public enum AccessProfileProjectSourceEvaluationState
{
    Supported,
    Unsupported,
    Error
}

/// <summary>
/// Result of profile-specific configuration validation.
/// </summary>
public sealed record AccessProfileValidationResult(
    bool IsValid,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Result of profile registration.
/// </summary>
public sealed record AccessProfileRegistrationResult(
    AccessProfileRegistrationState State,
    AccessProfileCapabilities Capabilities,
    string? Message);

/// <summary>
/// Result of profile session loading.
/// </summary>
public sealed record AccessProfileSessionLoadResult(
    bool IsSuccessful,
    IAccessProfileSession? Session,
    string? Message,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static AccessProfileSessionLoadResult Success(IAccessProfileSession session, string? message = null)
    {
        return new AccessProfileSessionLoadResult(true, session, message, Array.Empty<Diagnostic>());
    }

    public static AccessProfileSessionLoadResult Failure(string message, IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        return new AccessProfileSessionLoadResult(false, null, message, diagnostics ?? Array.Empty<Diagnostic>());
    }
}

/// <summary>
/// Event arguments for profile state changes.
/// </summary>
public sealed record ProfileStateChangedEventArgs(
    AccessProfileState State,
    string Reason);

/// <summary>
/// Coarse lifecycle state of a loaded access profile.
/// </summary>
public enum AccessProfileState
{
    Created,
    Ready,
    Invalidated,
    Disposed
}
