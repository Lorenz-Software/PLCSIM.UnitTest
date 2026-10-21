namespace PLCSIM.UnitTest.Common.Services;

/// <summary>
/// Application-facing health of an access profile or one of its functional services.
/// </summary>
public sealed record AccessHealth(
    AccessHealthState State,
    AccessHealthProblem Problem,
    string ReasonCode,
    string Message,
    DateTimeOffset UpdatedAt)
{
    public static AccessHealth NotEstablished(DateTimeOffset updatedAt)
    {
        return new AccessHealth(
            AccessHealthState.NotEstablished,
            AccessHealthProblem.None,
            "AccessHealth.NotEstablished",
            "Access context is not established.",
            updatedAt);
    }

    public static AccessHealth Healthy(DateTimeOffset updatedAt)
    {
        return new AccessHealth(
            AccessHealthState.Healthy,
            AccessHealthProblem.None,
            "AccessHealth.Healthy",
            "Access context is healthy.",
            updatedAt);
    }

    public static AccessHealth Disposed(DateTimeOffset updatedAt)
    {
        return new AccessHealth(
            AccessHealthState.Disposed,
            AccessHealthProblem.Released,
            "AccessHealth.Disposed",
            "Access context is disposed.",
            updatedAt);
    }
}

public sealed record AccessHealthChangedEventArgs(
    IReadOnlyList<AccessServiceHealthUpdate> Updates);

public sealed record AccessServiceHealthUpdate(
    AccessServiceKind ServiceKind,
    AccessHealth Health);

public enum AccessServiceKind
{
    Profile,
    Project,
    PlcControl,
    UnitTestLifecycle,
    UnitTestEventStream
}

public enum AccessHealthState
{
    NotEstablished,
    Healthy,
    RevalidationRequired,
    Invalidated,
    Disposed
}

public enum AccessHealthProblem
{
    None,
    ContextLost,
    ContractInvalid,
    CommunicationLost,
    RevalidationRequired,
    Released,
    InternalFailure
}
