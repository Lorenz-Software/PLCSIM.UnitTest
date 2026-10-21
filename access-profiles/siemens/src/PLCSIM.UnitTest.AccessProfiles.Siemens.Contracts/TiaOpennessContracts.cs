namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

using System.Security;

public interface ITiaEngineeringSession
{
    TiaProjectSessionInfo Project { get; }

    TiaProjectHealthState Health { get; }

    event EventHandler<TiaProjectHealthChangedEventArgs>? HealthChanged;

    event EventHandler<TiaProgressSnapshot>? ProgressChanged;

    Task<TiaProjectTargetDiscoveryResult> DiscoverTargetsAsync(
        TiaProjectTargetDiscoveryRequest request,
        CancellationToken cancellationToken);

    Task<TiaBuildResult> BuildAsync(
        TiaBuildRequest request,
        CancellationToken cancellationToken);

    Task<TiaTransferResult> TransferAsync(
        TiaTransferRequest request,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional project-session boundary for TIA versions that support offline Safety-program login.
/// </summary>
public interface ITiaSafetyProgramAuthorizationSession
{
    Task<TiaSafetyProgramAuthorizationRequirementResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaSafetyProgramAuthorizationResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        SecureString password,
        CancellationToken cancellationToken);
}

public sealed record TiaProjectHealthChangedEventArgs(
    TiaProjectHealthState Previous,
    TiaProjectHealthState Current,
    string Message);
