namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

public interface IPlcsimRuntimeSession
{
    PlcsimRuntimeSessionInfo Runtime { get; }

    PlcsimRuntimeHealthState Health { get; }

    event EventHandler<PlcsimRuntimeHealthChangedEventArgs>? HealthChanged;

    event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged;

    event EventHandler<PlcsimRuntimeProgressSnapshot>? ProgressChanged;

    Task<PlcsimRuntimeDiscoveryResult> DiscoverRuntimeTargetsAsync(
        PlcsimRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimePrepareResult> PrepareRuntimeAsync(
        PlcsimRuntimePrepareRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeObservationResult> ObserveRuntimeAsync(
        PlcsimRuntimeObservationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeObservationResult> RevalidateRuntimeAsync(
        PlcsimRuntimeRevalidationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
        PlcsimRuntimeReleaseRequest request,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public interface IPlcsimUnitTestCommunicationSession
{
    PlcsimCommunicationSessionInfo Communication { get; }

    PlcsimCommunicationHealthState Health { get; }

    event EventHandler<PlcsimCommunicationHealthChangedEventArgs>? HealthChanged;

    event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;

    event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived;

    event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged;

    Task<PlcsimRunControlValidationResult> ValidateRunControlAsync(
        PlcsimRunControlValidationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
        PlcsimRunControlCommandRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
        PlcsimEventBufferResetRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
        PlcsimRunControlObservationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimEventBufferValidationResult> ValidateEventBufferAsync(
        PlcsimEventBufferValidationRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
        PlcsimEventStreamMonitoringRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
        PlcsimEventStreamStopRequest request,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public sealed record PlcsimRuntimeHealthChangedEventArgs(
    PlcsimRuntimeHealthState Previous,
    PlcsimRuntimeHealthState Current,
    string Message);

public sealed record PlcsimRuntimeStateChangedEventArgs(
    PlcsimRuntimeStateChange Change);

public sealed record PlcsimCommunicationHealthChangedEventArgs(
    PlcsimCommunicationHealthState Previous,
    PlcsimCommunicationHealthState Current,
    string Message);

public sealed record PlcsimRunControlObservationChangedEventArgs(
    PlcsimRunControlObservation Observation);

public sealed record PlcsimEventStreamMonitoringStateChangedEventArgs(
    PlcsimEventStreamMonitoringState State);
