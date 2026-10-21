using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;

internal interface IPlcsimAdvancedV6WorkerSessionHost
{
    Task<PlcsimAdvancedV6WorkerSessionHostOpenResult> OpenAsync(
        string runtimeApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal interface IPlcsimAdvancedV6WorkerSessionHandle
{
    string SessionId { get; }

    event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged;

    event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived;

    event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged;

    event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;

    event EventHandler<PlcsimWorkerCommunicationHealthChangedEventArgs>? CommunicationHealthChanged;

    event EventHandler<PlcsimEventStreamMonitoringFaultedEventArgs>? EventStreamMonitoringFaulted;

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

    Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        PlcsimCommunicationPrepareRequest request,
        CancellationToken cancellationToken);

    Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
        PlcsimRunControlObservationRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken);

    Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
        PlcsimRunControlCommandRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken);

    Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
        PlcsimEventBufferResetRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken);

    Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
        PlcsimEventStreamMonitoringRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken);

    Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
        PlcsimEventStreamStopRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken);

    Task<PlcsimCommunicationCloseResult> CloseCommunicationAsync(
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken);

    void Terminate(string message);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class PlcsimAdvancedV6WorkerSessionHost : IPlcsimAdvancedV6WorkerSessionHost
{
    private static readonly TimeSpan WorkerCommandResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly PlcsimAdvancedV6WorkerBootstrap bootstrap;
    private readonly TimeSpan commandResponseTimeout;

    public PlcsimAdvancedV6WorkerSessionHost()
        : this(
            PlcsimAdvancedV6WorkerBootstrap.ResolveWorkerPath,
            WorkerCommandResponseTimeout)
    {
    }

    internal PlcsimAdvancedV6WorkerSessionHost(
        Func<string> workerPathResolver,
        TimeSpan? commandResponseTimeout = null)
    {
        bootstrap = new PlcsimAdvancedV6WorkerBootstrap(workerPathResolver);
        this.commandResponseTimeout = commandResponseTimeout ?? WorkerCommandResponseTimeout;
    }

    public async Task<PlcsimAdvancedV6WorkerSessionHostOpenResult> OpenAsync(
        string runtimeApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        var result = await bootstrap.OpenAsync(
            runtimeApiDirectory,
            bindingContext,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return PlcsimAdvancedV6WorkerSessionHostOpenResult.Failed(
                result.Diagnostics[0],
                result.Evidence);
        }

        var connection = result.Connection!;
        try
        {
            return PlcsimAdvancedV6WorkerSessionHostOpenResult.SucceededResult(
                new PlcsimAdvancedV6WorkerSessionHandle(
                    connection.Process,
                    connection.Pipe,
                    connection.Reader,
                    connection.Writer,
                    connection.SessionId,
                    commandResponseTimeout),
                result.Evidence);
        }
        catch (Exception ex)
        {
            connection.Dispose();
            return PlcsimAdvancedV6WorkerSessionHostOpenResult.Failed(
                new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.WorkerStartFailed",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 worker session handle could not be created.",
                    "OpenWorkerSession",
                    "plcsim-advanced-v6-worker-host"),
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-worker-error",
                        "WorkerError",
                        ex.Message)
                });
        }
    }
}

internal sealed record PlcsimAdvancedV6WorkerSessionHostOpenResult(
    PlcsimOperationStatus Status,
    IPlcsimAdvancedV6WorkerSessionHandle? Session,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;

    public static PlcsimAdvancedV6WorkerSessionHostOpenResult SucceededResult(
        IPlcsimAdvancedV6WorkerSessionHandle session,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6WorkerSessionHostOpenResult(
            PlcsimOperationStatus.Succeeded,
            session ?? throw new ArgumentNullException(nameof(session)),
            Array.Empty<PlcsimDiagnostic>(),
            evidence);
    }

    public static PlcsimAdvancedV6WorkerSessionHostOpenResult Failed(
        PlcsimDiagnostic diagnostic,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6WorkerSessionHostOpenResult(
            PlcsimOperationStatus.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal sealed record PlcsimEventStreamMonitoringFaultedEventArgs(
    string CommunicationSessionId,
    PlcsimOperationStatus Status,
    PlcsimDiagnostic Diagnostic);

internal sealed record PlcsimWorkerCommunicationHealthChangedEventArgs(
    string CommunicationSessionId,
    PlcsimCommunicationHealthState Health,
    string Message);

internal sealed class PlcsimAdvancedV6WorkerSessionHandle : IPlcsimAdvancedV6WorkerSessionHandle
{
    private readonly PlcsimAdvancedV6WorkerTransport transport;
    private readonly PlcsimAdvancedV6WorkerEventRouter eventRouter;
    private readonly PlcsimAdvancedV6WorkerMessageMapper mapper;

    public PlcsimAdvancedV6WorkerSessionHandle(
        Process process,
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer,
        string sessionId,
        TimeSpan commandResponseTimeout)
    {
        ArgumentNullException.ThrowIfNull(process);
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? process.Id.ToString() : sessionId;
        mapper = new PlcsimAdvancedV6WorkerMessageMapper(SessionId);
        eventRouter = new PlcsimAdvancedV6WorkerEventRouter(this);
        transport = new PlcsimAdvancedV6WorkerTransport(
            process,
            pipe,
            reader,
            writer,
            commandResponseTimeout,
            TryHandleAsyncMessage,
            HandleConnectionLost);
    }

    public string SessionId { get; }

    internal int WorkerProcessId => transport.WorkerProcessId;

    public event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged
    {
        add => eventRouter.RuntimeStateChanged += value;
        remove => eventRouter.RuntimeStateChanged -= value;
    }

    public event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived
    {
        add => eventRouter.EventBatchReceived += value;
        remove => eventRouter.EventBatchReceived -= value;
    }

    public event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged
    {
        add => eventRouter.EventStreamMonitoringStateChanged += value;
        remove => eventRouter.EventStreamMonitoringStateChanged -= value;
    }

    public event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged
    {
        add => eventRouter.RunControlObservationChanged += value;
        remove => eventRouter.RunControlObservationChanged -= value;
    }

    public event EventHandler<PlcsimWorkerCommunicationHealthChangedEventArgs>? CommunicationHealthChanged
    {
        add => eventRouter.CommunicationHealthChanged += value;
        remove => eventRouter.CommunicationHealthChanged -= value;
    }

    public event EventHandler<PlcsimEventStreamMonitoringFaultedEventArgs>? EventStreamMonitoringFaulted
    {
        add => eventRouter.EventStreamMonitoringFaulted += value;
        remove => eventRouter.EventStreamMonitoringFaulted -= value;
    }

    private bool IsDisposed => transport.IsDisposed;

    public async Task<PlcsimRuntimeDiscoveryResult> DiscoverRuntimeTargetsAsync(
        PlcsimRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedDiscovery(
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for runtime discovery.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            await transport.SendRequestAsync(transport.CreateCommandRequest("DiscoverRuntimeTargets"), cancellationToken).ConfigureAwait(false);

            var targets = new List<PlcsimRuntimeTargetCandidate>();
            var evidence = new List<PlcsimEvidence>
            {
                new(
                    "plcsim-advanced-v6-runtime-discovery",
                    "RuntimeDiscovery",
                    SessionId)
            };

            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedDiscovery(
                        "PlcsimAdvanced.V6.RuntimeDiscoveryWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during runtime discovery.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedDiscovery(
                        "PlcsimAdvanced.V6.RuntimeDiscoveryProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during runtime discovery.");
                }

                if (string.Equals(message.Type, "RuntimeTarget", StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(mapper.CreateRuntimeTarget(message.Values));
                    continue;
                }

                if (string.Equals(message.Type, "EndRuntimeTargets", StringComparison.OrdinalIgnoreCase))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimRuntimeDiscoveryResult(
                            PlcsimOperationStatus.Cancelled,
                            Array.Empty<PlcsimRuntimeTargetCandidate>(),
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    "PlcsimAdvanced.V6.RuntimeDiscoveryCancelled",
                                    "PLCSIM Advanced V6 runtime discovery was cancelled after the worker response was drained.",
                                    "DiscoverRuntimeTargets",
                                    "plcsim-advanced-v6-runtime-discovery")
                            },
                            evidence);
                    }

                    var status = mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Unclear);
                    return new PlcsimRuntimeDiscoveryResult(
                        status,
                        targets,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimRuntimeDiscoveryResult(
                        PlcsimOperationStatus.Failed,
                        Array.Empty<PlcsimRuntimeTargetCandidate>(),
                        new[]
                        {
                            new PlcsimDiagnostic(
                                message.Values.TryGetValue("code", out var code) ? code : "PlcsimAdvanced.V6.RuntimeDiscoveryWorkerError",
                                PlcsimDiagnosticSeverity.Error,
                                message.Values.TryGetValue("message", out var workerMessage)
                                    ? workerMessage
                                    : "PLCSIM Advanced V6 worker reported a runtime discovery error.",
                                "DiscoverRuntimeTargets",
                                "plcsim-advanced-v6-runtime-discovery")
                        },
                        evidence);
                }

                return mapper.FailedDiscovery(
                    "PlcsimAdvanced.V6.RuntimeDiscoveryUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected runtime discovery response.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return new PlcsimRuntimeDiscoveryResult(
                PlcsimOperationStatus.Cancelled,
                Array.Empty<PlcsimRuntimeTargetCandidate>(),
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RuntimeDiscoveryCancelled",
                        PlcsimDiagnosticSeverity.Warning,
                        "PLCSIM Advanced V6 runtime discovery was cancelled.",
                        "DiscoverRuntimeTargets",
                        "plcsim-advanced-v6-runtime-discovery")
                },
                Array.Empty<PlcsimEvidence>());
        }
        catch (TimeoutException)
        {
            Dispose();
            return new PlcsimRuntimeDiscoveryResult(
                PlcsimOperationStatus.TimedOut,
                Array.Empty<PlcsimRuntimeTargetCandidate>(),
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RuntimeDiscoveryTimedOut",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker did not complete runtime discovery before the command timeout.",
                        "DiscoverRuntimeTargets",
                        "plcsim-advanced-v6-runtime-discovery")
                },
                Array.Empty<PlcsimEvidence>());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRuntimeDiscoveryResult(
                PlcsimOperationStatus.Failed,
                Array.Empty<PlcsimRuntimeTargetCandidate>(),
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RuntimeDiscoveryIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker IPC failed during runtime discovery.",
                        "DiscoverRuntimeTargets",
                        "plcsim-advanced-v6-runtime-discovery")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runtime-discovery-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public async Task<PlcsimRuntimePrepareResult> PrepareRuntimeAsync(
        PlcsimRuntimePrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedPrepare(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for runtime preparation.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            await transport.SendRequestAsync(transport.CreateCommandRequest(
                "PrepareRuntime",
                new KeyValuePair<string, string>("id", request.RuntimeReference?.Id ?? string.Empty),
                new KeyValuePair<string, string>("instanceName", request.RuntimeReference?.InstanceName ?? string.Empty),
                new KeyValuePair<string, string>("storagePath", request.StoragePreference.StoragePath ?? string.Empty),
                new KeyValuePair<string, string>("provisioningMode", request.ProvisioningMode.ToString()),
                new KeyValuePair<string, string>("cpuType", request.CpuType.ToString()),
                new KeyValuePair<string, string>("timeoutMs", ((int)request.Timeout.TotalMilliseconds).ToString())), cancellationToken).ConfigureAwait(false);

            var evidence = new List<PlcsimEvidence>
            {
                new(
                    "plcsim-advanced-v6-runtime-prepare",
                    "RuntimePrepare",
                    SessionId)
            };

            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(
                    CancellationToken.None,
                    request.Timeout).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedPrepare(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimePrepareWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during runtime preparation.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedPrepare(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimePrepareProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during runtime preparation.");
                }

                if (string.Equals(message.Type, "RuntimePrepared", StringComparison.OrdinalIgnoreCase))
                {
                    var status = mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded);
                    var runtime = mapper.CreateRuntimeTarget(message.Values);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimRuntimePrepareResult(
                            PlcsimOperationStatus.Cancelled,
                            runtime,
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    "PlcsimAdvanced.V6.RuntimePrepareCancelled",
                                    "PLCSIM Advanced V6 runtime preparation completed after cancellation; the prepared runtime remains owned by the runtime session.",
                                    "PrepareRuntime",
                                    "plcsim-advanced-v6-runtime-prepare")
                            },
                            evidence);
                    }

                    return new PlcsimRuntimePrepareResult(
                        status,
                        runtime,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var result = mapper.MapRuntimePrepareFailure(message.Values, evidence);
                    if (mapper.HasIncompleteRuntimePrepareCompensation(message.Values))
                    {
                        Terminate(
                            "PLCSIM Advanced V6 worker session was invalidated after incomplete owned-runtime compensation.");
                    }

                    return result;
                }

                return mapper.FailedPrepare(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RuntimePrepareUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected runtime preparation response.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedPrepare(
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.RuntimePrepareCancelled",
                "PLCSIM Advanced V6 runtime preparation was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedPrepare(
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.RuntimePrepareTimedOut",
                "PLCSIM Advanced V6 worker did not complete runtime preparation before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRuntimePrepareResult(
                PlcsimOperationStatus.Failed,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RuntimePrepareIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker IPC failed during runtime preparation.",
                        "PrepareRuntime",
                        "plcsim-advanced-v6-runtime-prepare")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runtime-prepare-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public Task<PlcsimRuntimeObservationResult> ObserveRuntimeAsync(
        PlcsimRuntimeObservationRequest request,
        CancellationToken cancellationToken)
    {
        return ObserveRuntimeCoreAsync(request, cancellationToken);
    }

    public Task<PlcsimRuntimeObservationResult> RevalidateRuntimeAsync(
        PlcsimRuntimeRevalidationRequest request,
        CancellationToken cancellationToken)
    {
        return RevalidateRuntimeCoreAsync(request, cancellationToken);
    }

    public Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken)
    {
        return ControlRuntimeCoreAsync("StartRuntime", request, cancellationToken);
    }

    public Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken)
    {
        return ControlRuntimeCoreAsync("StopRuntime", request, cancellationToken);
    }

    private async Task<PlcsimRuntimeObservationResult> ObserveRuntimeCoreAsync(
        PlcsimRuntimeObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedObservation(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for runtime observation.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            await transport.SendRequestAsync(transport.CreateCommandRequest("ObserveRuntime"), cancellationToken).ConfigureAwait(false);

            var evidence = mapper.CreateRuntimeEvidence("plcsim-advanced-v6-runtime-observation", "RuntimeObservation");
            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedObservation(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimeObservationWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during runtime observation.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedObservation(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimeObservationProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during runtime observation.");
                }

                if (string.Equals(message.Type, "RuntimeObserved", StringComparison.OrdinalIgnoreCase))
                {
                    var runtime = mapper.CreateRuntimeTarget(message.Values);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimRuntimeObservationResult(
                            PlcsimOperationStatus.Cancelled,
                            runtime,
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    "PlcsimAdvanced.V6.RuntimeObservationCancelled",
                                    "PLCSIM Advanced V6 runtime observation was cancelled after the worker response was drained.",
                                    "ObserveRuntime",
                                    "plcsim-advanced-v6-runtime-observation")
                            },
                            evidence);
                    }

                    return new PlcsimRuntimeObservationResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                        runtime,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimRuntimeObservationResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        null,
                        new[]
                        {
                            mapper.CreateWorkerDiagnostic(
                                message.Values,
                                "PlcsimAdvanced.V6.RuntimeObservationWorkerError",
                                "PLCSIM Advanced V6 worker reported a runtime observation error.",
                                "ObserveRuntime",
                                "plcsim-advanced-v6-runtime-observation")
                        },
                        evidence);
                }

                return mapper.FailedObservation(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RuntimeObservationUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected runtime observation response.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedObservation(
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.RuntimeObservationCancelled",
                "PLCSIM Advanced V6 runtime observation was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedObservation(
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.RuntimeObservationTimedOut",
                "PLCSIM Advanced V6 worker did not complete runtime observation before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRuntimeObservationResult(
                PlcsimOperationStatus.Failed,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RuntimeObservationIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        $"PLCSIM Advanced V6 runtime observation IPC failed: {ex.GetBaseException().Message}",
                        "ObserveRuntime",
                        "plcsim-advanced-v6-runtime-observation")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runtime-observation-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    private async Task<PlcsimRuntimeObservationResult> RevalidateRuntimeCoreAsync(
        PlcsimRuntimeRevalidationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedObservation(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for runtime revalidation.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            await transport.SendRequestAsync(transport.CreateCommandRequest(
                    "RevalidateRuntime",
                    new KeyValuePair<string, string>("expectedRuntimeId", request.ExpectedRuntime.Id ?? string.Empty),
                    new KeyValuePair<string, string>("expectedInstanceName", request.ExpectedRuntime.InstanceName ?? string.Empty),
                    new KeyValuePair<string, string>("timeoutMs", ((int)request.Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("correlationId", request.CorrelationId ?? string.Empty)), cancellationToken).ConfigureAwait(false);

            var evidence = mapper.CreateRuntimeEvidence("plcsim-advanced-v6-runtime-revalidation", "RuntimeRevalidation");
            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedObservation(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimeRevalidationWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during runtime revalidation.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedObservation(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimeRevalidationProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during runtime revalidation.");
                }

                if (string.Equals(message.Type, "RuntimeRevalidated", StringComparison.OrdinalIgnoreCase))
                {
                    var runtime = mapper.CreateRuntimeTarget(message.Values);
                    return new PlcsimRuntimeObservationResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                        runtime,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimRuntimeObservationResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        null,
                        new[]
                        {
                            mapper.CreateWorkerDiagnostic(
                                message.Values,
                                "PlcsimAdvanced.V6.RuntimeRevalidationWorkerError",
                                "PLCSIM Advanced V6 worker reported a runtime revalidation error.",
                                "RevalidateRuntime",
                                "plcsim-advanced-v6-runtime-revalidation")
                        },
                        evidence);
                }

                return mapper.FailedObservation(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RuntimeRevalidationUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected runtime revalidation response.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedObservation(
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.RuntimeRevalidationCancelled",
                "PLCSIM Advanced V6 runtime revalidation was cancelled after dispatch.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedObservation(
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.RuntimeRevalidationTimedOut",
                "PLCSIM Advanced V6 worker did not complete runtime revalidation before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRuntimeObservationResult(
                PlcsimOperationStatus.Failed,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RuntimeRevalidationIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        $"PLCSIM Advanced V6 runtime revalidation IPC failed: {ex.GetBaseException().Message}",
                        "RevalidateRuntime",
                        "plcsim-advanced-v6-runtime-revalidation")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runtime-revalidation",
                        "RuntimeRevalidation",
                        ex.GetBaseException().Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    private async Task<PlcsimRuntimeControlResult> ControlRuntimeCoreAsync(
        string command,
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedControl(
                command,
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                $"PLCSIM Advanced V6 worker is not available for {command}.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            await transport.SendRequestAsync(transport.CreateCommandRequest(
                command,
                new KeyValuePair<string, string>("timeoutMs", ((int)request.Timeout.TotalMilliseconds).ToString())), cancellationToken).ConfigureAwait(false);

            var evidenceId = command == "StartRuntime"
                ? "plcsim-advanced-v6-runtime-start"
                : "plcsim-advanced-v6-runtime-stop";
            var evidence = mapper.CreateRuntimeEvidence(evidenceId, command);
            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(
                    CancellationToken.None,
                    request.Timeout).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedControl(
                        command,
                        PlcsimOperationStatus.Failed,
                        $"PlcsimAdvanced.V6.{command}WorkerDisconnected",
                        $"PLCSIM Advanced V6 worker disconnected during {command}.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedControl(
                        command,
                        PlcsimOperationStatus.Failed,
                        $"PlcsimAdvanced.V6.{command}ProtocolUnsupported",
                        $"PLCSIM Advanced V6 worker returned an unsupported protocol response during {command}.");
                }

                if (string.Equals(message.Type, "RuntimeControlled", StringComparison.OrdinalIgnoreCase))
                {
                    var state = mapper.ReadRuntimeControlState(message.Values, "state");
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimRuntimeControlResult(
                            PlcsimOperationStatus.Cancelled,
                            state,
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    $"PlcsimAdvanced.V6.{command}Cancelled",
                                    $"PLCSIM Advanced V6 {command} completed after cancellation; runtime state remains event-observed.",
                                    command,
                                    evidenceId)
                            },
                            evidence);
                    }

                    return new PlcsimRuntimeControlResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                        state,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimRuntimeControlResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        mapper.ReadRuntimeControlState(message.Values, "state"),
                        new[]
                        {
                            mapper.CreateWorkerDiagnostic(
                                message.Values,
                                $"PlcsimAdvanced.V6.{command}WorkerError",
                                $"PLCSIM Advanced V6 worker reported a {command} error.",
                                command,
                                evidenceId)
                        },
                        evidence);
                }

                return mapper.FailedControl(
                    command,
                    PlcsimOperationStatus.Failed,
                    $"PlcsimAdvanced.V6.{command}UnexpectedResponse",
                    $"PLCSIM Advanced V6 worker returned an unexpected {command} response.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedControl(
                command,
                PlcsimOperationStatus.Cancelled,
                $"PlcsimAdvanced.V6.{command}Cancelled",
                $"PLCSIM Advanced V6 {command} was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedControl(
                command,
                PlcsimOperationStatus.TimedOut,
                $"PlcsimAdvanced.V6.{command}TimedOut",
                $"PLCSIM Advanced V6 worker did not complete {command} before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRuntimeControlResult(
                PlcsimOperationStatus.Failed,
                PlcsimRuntimeControlState.Unknown,
                new[]
                {
                    new PlcsimDiagnostic(
                        $"PlcsimAdvanced.V6.{command}IpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        $"PLCSIM Advanced V6 {command} IPC failed: {ex.GetBaseException().Message}",
                        command,
                        command == "StartRuntime" ? "plcsim-advanced-v6-runtime-start" : "plcsim-advanced-v6-runtime-stop")
                },
                new[]
                {
                    new PlcsimEvidence(
                        $"plcsim-advanced-v6-{command.ToLowerInvariant()}-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public async Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
        PlcsimRuntimeReleaseRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedRelease(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for runtime release.",
                request.CleanupPreference);
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            await transport.SendRequestAsync(transport.CreateCommandRequest(
                "ReleaseRuntime",
                new KeyValuePair<string, string>("sessionId", request.SessionId),
                new KeyValuePair<string, string>("cleanupPreference", request.CleanupPreference.ToString()),
                new KeyValuePair<string, string>("timeoutMs", ((int)request.Timeout.TotalMilliseconds).ToString())), cancellationToken).ConfigureAwait(false);

            var evidence = new List<PlcsimEvidence>
            {
                new(
                    "plcsim-advanced-v6-runtime-release",
                    "RuntimeRelease",
                    SessionId)
            };

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await transport.ReadCommandResponseLineAsync(
                    cancellationToken,
                    request.Timeout).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedRelease(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimeReleaseWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during runtime release.",
                        request.CleanupPreference);
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedRelease(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RuntimeReleaseProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during runtime release.",
                        request.CleanupPreference);
                }

                if (string.Equals(message.Type, "RuntimeReleased", StringComparison.OrdinalIgnoreCase))
                {
                    var status = mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded);
                    return new PlcsimRuntimeReleaseResult(
                        status,
                        mapper.ReadBool(message.Values, "released"),
                        mapper.ReadCleanupPreference(message.Values, "cleanupPreference", request.CleanupPreference),
                        mapper.ReadBool(message.Values, "storageCleaned"),
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimRuntimeReleaseResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        false,
                        request.CleanupPreference,
                        false,
                        new[]
                        {
                            new PlcsimDiagnostic(
                                message.Values.TryGetValue("code", out var code) ? code : "PlcsimAdvanced.V6.RuntimeReleaseWorkerError",
                                PlcsimDiagnosticSeverity.Error,
                                message.Values.TryGetValue("message", out var workerMessage)
                                    ? workerMessage
                                    : "PLCSIM Advanced V6 worker reported a runtime release error.",
                                "ReleaseRuntime",
                                "plcsim-advanced-v6-runtime-release")
                        },
                        evidence);
                }

                return mapper.FailedRelease(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RuntimeReleaseUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected runtime release response.",
                    request.CleanupPreference);
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedRelease(
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.RuntimeReleaseCancelled",
                "PLCSIM Advanced V6 runtime release was cancelled.",
                request.CleanupPreference,
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedRelease(
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.RuntimeReleaseTimedOut",
                "PLCSIM Advanced V6 worker did not complete runtime release before the command timeout.",
                request.CleanupPreference);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRuntimeReleaseResult(
                PlcsimOperationStatus.Failed,
                false,
                request.CleanupPreference,
                false,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RuntimeReleaseIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker IPC failed during runtime release.",
                        "ReleaseRuntime",
                        "plcsim-advanced-v6-runtime-release")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runtime-release-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public async Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        PlcsimCommunicationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitoringInterval = request.WorkerOptions.MonitoringInterval
            ?? (request.WorkerOptions.ObservationStrategy == PlcsimCommunicationObservationStrategyKind.Polling
                ? TimeSpan.FromMilliseconds(10)
                : (TimeSpan?)null);
        if (monitoringInterval is { } configuredMonitoringInterval
            && configuredMonitoringInterval <= TimeSpan.Zero)
        {
            return mapper.FailedCommunicationPrepare(
                PlcsimOperationStatus.Rejected,
                "PlcsimAdvanced.V6.CommunicationMonitoringIntervalInvalid",
                "PLCSIM Advanced V6 monitoring requires a positive interval.");
        }

        if (request.WorkerOptions.DrainInterval is { } drainInterval && drainInterval <= TimeSpan.Zero)
        {
            return mapper.FailedCommunicationPrepare(
                PlcsimOperationStatus.Rejected,
                "PlcsimAdvanced.V6.CommunicationDrainIntervalInvalid",
                "PLCSIM Advanced V6 draining requires a positive interval when configured.");
        }

        if (request.WorkerOptions.MaxEventsPerBatch is { } maxEventsPerBatch && maxEventsPerBatch <= 0)
        {
            return mapper.FailedCommunicationPrepare(
                PlcsimOperationStatus.Rejected,
                "PlcsimAdvanced.V6.CommunicationMaxEventsPerBatchInvalid",
                "PLCSIM Advanced V6 communication requires maxEventsPerBatch to be greater than zero when configured.");
        }

        if (request.PrepareTimeout <= TimeSpan.Zero || request.RevalidationTimeout < TimeSpan.Zero)
        {
            return mapper.FailedCommunicationPrepare(
                PlcsimOperationStatus.Rejected,
                "PlcsimAdvanced.V6.CommunicationTimeoutInvalid",
                "PLCSIM Advanced V6 communication preparation requires a positive prepare timeout and a non-negative revalidation timeout.");
        }

        if (!transport.IsAvailable)
        {
            return mapper.FailedCommunicationPrepare(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for UnitTest communication preparation.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            var commandValues = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("runtimeSessionId", request.RuntimeSessionId),
                new KeyValuePair<string, string>("runControlRoot", request.RunControlRoot),
                new KeyValuePair<string, string>("eventBufferRoot", request.EventBufferRoot),
                new KeyValuePair<string, string>("expectedRunControlContractVersion", request.ExpectedRunControlContractVersion),
                new KeyValuePair<string, string>("expectedEventStreamContractVersion", request.ExpectedEventStreamContractVersion),
                new KeyValuePair<string, string>("observationStrategy", request.WorkerOptions.ObservationStrategy.ToString()),
                new KeyValuePair<string, string>("drainIntervalTicks", (request.WorkerOptions.DrainInterval?.Ticks ?? 0).ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("maxEventsPerBatch", (request.WorkerOptions.MaxEventsPerBatch ?? 0).ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("prepareTimeoutMs", ((int)request.PrepareTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("revalidationTimeoutMs", ((int)request.RevalidationTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))
            };
            if (monitoringInterval is { } serializedMonitoringInterval)
            {
                var ticks = serializedMonitoringInterval.Ticks.ToString(CultureInfo.InvariantCulture);
                commandValues.Add(new KeyValuePair<string, string>("monitoringIntervalTicks", ticks));
                commandValues.Add(new KeyValuePair<string, string>("pollIntervalTicks", ticks));
            }

            await transport.SendRequestAsync(
                transport.CreateCommandRequest("PrepareCommunication", commandValues.ToArray()),
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(
                    CancellationToken.None,
                    request.PrepareTimeout).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedCommunicationPrepare(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.CommunicationPrepareWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during UnitTest communication preparation.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedCommunicationPrepare(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.CommunicationPrepareProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during UnitTest communication preparation.");
                }

                if (string.Equals(message.Type, "CommunicationPrepared", StringComparison.OrdinalIgnoreCase))
                {
                    var status = mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimCommunicationPrepareResult(
                            PlcsimOperationStatus.Cancelled,
                            null,
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    "PlcsimAdvanced.V6.CommunicationPrepareCancelled",
                                    "PLCSIM Advanced V6 UnitTest communication preparation completed after cancellation; the worker remains reusable and a later prepare request will replace the retained communication state.",
                                    "PrepareCommunication",
                                    "plcsim-advanced-v6-communication-prepare")
                            },
                            mapper.CreateRuntimeEvidence("plcsim-advanced-v6-communication-prepare", "CommunicationPrepare"));
                    }

                    var communicationSessionId = message.Values.TryGetValue("communicationSessionId", out var sessionId)
                        && !string.IsNullOrWhiteSpace(sessionId)
                            ? sessionId
                            : Guid.NewGuid().ToString("N");
                    var preparedAt = DateTimeOffset.UtcNow;
                    var session = new PlcsimAdvancedV6CommunicationSession(
                        this,
                        new PlcsimCommunicationSessionInfo(
                            communicationSessionId,
                            request.RuntimeSessionId,
                            request.RunControlRoot,
                            request.EventBufferRoot,
                            preparedAt),
                        message.Values.TryGetValue("runControlContractVersion", out var runControlContractVersion)
                            ? runControlContractVersion
                            : null,
                        message.Values.TryGetValue("runControlFrameworkVersion", out var runControlFrameworkVersion)
                            ? runControlFrameworkVersion
                            : null,
                        message.Values.TryGetValue("eventStreamContractVersion", out var eventStreamContractVersion)
                            ? eventStreamContractVersion
                            : null,
                        message.Values.TryGetValue("eventStreamFrameworkVersion", out var eventStreamFrameworkVersion)
                            ? eventStreamFrameworkVersion
                            : null);
                    return new PlcsimCommunicationPrepareResult(
                        status,
                        session,
                        Array.Empty<PlcsimDiagnostic>(),
                        mapper.CreateRuntimeEvidence("plcsim-advanced-v6-communication-prepare", "CommunicationPrepare"));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimCommunicationPrepareResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        null,
                        new[]
                        {
                            mapper.CreateWorkerDiagnostic(
                                message.Values,
                                "PlcsimAdvanced.V6.CommunicationPrepareFailed",
                                "PLCSIM Advanced V6 worker reported a UnitTest communication preparation error.",
                                "PrepareCommunication",
                                "plcsim-advanced-v6-communication-prepare")
                        },
                        Array.Empty<PlcsimEvidence>());
                }

                return mapper.FailedCommunicationPrepare(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.CommunicationPrepareUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected response during UnitTest communication preparation.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedCommunicationPrepare(
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.CommunicationPrepareCancelled",
                "PLCSIM Advanced V6 UnitTest communication preparation was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedCommunicationPrepare(
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.CommunicationPrepareTimedOut",
                "PLCSIM Advanced V6 worker did not complete UnitTest communication preparation before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimCommunicationPrepareResult(
                PlcsimOperationStatus.Failed,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.CommunicationPrepareIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker IPC failed during UnitTest communication preparation.",
                        "PrepareCommunication",
                        "plcsim-advanced-v6-communication-prepare")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-communication-prepare-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public async Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
        PlcsimRunControlObservationRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedRunControlObservation(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for RunControl observation.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            var runtimeAvailability = await ObserveRuntimeAvailabilityUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (runtimeAvailability is not null)
            {
                return mapper.FailedRunControlObservation(
                    runtimeAvailability.Status,
                    runtimeAvailability.Code,
                    runtimeAvailability.Message);
            }

            await transport.SendRequestAsync(transport.CreateCommandRequest(
                "ObserveRunControl",
                new KeyValuePair<string, string>("runtimeSessionId", communication.RuntimeSessionId),
                new KeyValuePair<string, string>("communicationSessionId", request.CommunicationSessionId),
                new KeyValuePair<string, string>("runControlRoot", communication.RunControlRoot)), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedRunControlObservation(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RunControlObservationWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during RunControl observation.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedRunControlObservation(
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RunControlObservationProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during RunControl observation.");
                }

                if (string.Equals(message.Type, "RunControlObserved", StringComparison.OrdinalIgnoreCase))
                {
                    var observation = mapper.CreateRunControlObservation(message.Values);
                    var evidence = mapper.CreateRunControlObservationEvidence(message.Values);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimRunControlObservationResult(
                            PlcsimOperationStatus.Cancelled,
                            observation,
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    "PlcsimAdvanced.V6.RunControlObservationCancelled",
                                    "PLCSIM Advanced V6 RunControl observation was cancelled after the worker response was drained.",
                                    "ObserveRunControl",
                                    "plcsim-advanced-v6-runcontrol-observation")
                            },
                            evidence);
                    }

                    return new PlcsimRunControlObservationResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                        observation,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimRunControlObservationResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        null,
                        new[]
                        {
                            mapper.CreateWorkerDiagnostic(
                                message.Values,
                                "PlcsimAdvanced.V6.RunControlObservationFailed",
                                "PLCSIM Advanced V6 worker reported a RunControl observation error.",
                                "ObserveRunControl",
                                "plcsim-advanced-v6-runcontrol-observation")
                        },
                        Array.Empty<PlcsimEvidence>());
                }

                return mapper.FailedRunControlObservation(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RunControlObservationUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected response during RunControl observation.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedRunControlObservation(
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.RunControlObservationCancelled",
                "PLCSIM Advanced V6 RunControl observation was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedRunControlObservation(
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.RunControlObservationTimedOut",
                "PLCSIM Advanced V6 worker did not complete RunControl observation before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRunControlObservationResult(
                PlcsimOperationStatus.Failed,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RunControlObservationIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker IPC failed during RunControl observation.",
                        "ObserveRunControl",
                        "plcsim-advanced-v6-runcontrol-observation")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runcontrol-observation-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public async Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
        PlcsimRunControlCommandRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedRunControlCommand(
                request.Command,
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for RunControl commands.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            var runtimeAvailability = await ObserveRuntimeAvailabilityUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (runtimeAvailability is not null)
            {
                return mapper.FailedRunControlCommand(
                    request.Command,
                    runtimeAvailability.Status,
                    runtimeAvailability.Code,
                    runtimeAvailability.Message);
            }

            await transport.SendRequestAsync(transport.CreateCommandRequest(
                "RequestRunControlCommand",
                new KeyValuePair<string, string>("runtimeSessionId", communication.RuntimeSessionId),
                new KeyValuePair<string, string>("communicationSessionId", request.CommunicationSessionId),
                new KeyValuePair<string, string>("runControlRoot", communication.RunControlRoot),
                new KeyValuePair<string, string>("command", request.Command.ToString()),
                new KeyValuePair<string, string>("timeoutMs", ((int)request.Timeout.TotalMilliseconds).ToString())), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(
                    CancellationToken.None,
                    request.Timeout).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedRunControlCommand(
                        request.Command,
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RunControlCommandWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during RunControl command request.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedRunControlCommand(
                        request.Command,
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.RunControlCommandProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during RunControl command request.");
                }

                if (string.Equals(message.Type, "RunControlCommandRequested", StringComparison.OrdinalIgnoreCase))
                {
                    var accepted = mapper.ReadBool(message.Values, "accepted");
                    var evidence = mapper.CreateRunControlCommandEvidence(request.Command, message.Values);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimRunControlCommandResult(
                            PlcsimOperationStatus.Cancelled,
                            request.Command,
                            accepted,
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    "PlcsimAdvanced.V6.RunControlCommandCancelled",
                                    "PLCSIM Advanced V6 RunControl command completed after cancellation; the accepted flag reports whether the PLC write succeeded.",
                                    "RunControlCommand",
                                    "plcsim-advanced-v6-runcontrol-command")
                            },
                            evidence);
                    }

                    return new PlcsimRunControlCommandResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                        request.Command,
                        accepted,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimRunControlCommandResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        request.Command,
                        false,
                        new[]
                        {
                            mapper.CreateWorkerDiagnostic(
                                message.Values,
                                "PlcsimAdvanced.V6.RunControlCommandFailed",
                                "PLCSIM Advanced V6 worker reported a RunControl command error.",
                                "RunControlCommand",
                                "plcsim-advanced-v6-runcontrol-command")
                        },
                        Array.Empty<PlcsimEvidence>());
                }

                return mapper.FailedRunControlCommand(
                    request.Command,
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RunControlCommandUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected response during RunControl command request.");
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedRunControlCommand(
                request.Command,
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.RunControlCommandCancelled",
                "PLCSIM Advanced V6 RunControl command request was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedRunControlCommand(
                request.Command,
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.RunControlCommandTimedOut",
                "PLCSIM Advanced V6 worker did not complete RunControl command request before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimRunControlCommandResult(
                PlcsimOperationStatus.Failed,
                request.Command,
                false,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.RunControlCommandIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker IPC failed during RunControl command request.",
                        "RunControlCommand",
                        "plcsim-advanced-v6-runcontrol-command")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-runcontrol-command-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public async Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
        PlcsimEventBufferResetRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedEventBufferReset(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for EventBuffer reset commands.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            var runtimeAvailability = await ObserveRuntimeAvailabilityUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (runtimeAvailability is not null)
            {
                return mapper.FailedEventBufferReset(
                    runtimeAvailability.Status,
                    runtimeAvailability.Code,
                    runtimeAvailability.Message);
            }

            await transport.SendRequestAsync(transport.CreateCommandRequest(
                "RequestEventBufferReset",
                new KeyValuePair<string, string>("runtimeSessionId", communication.RuntimeSessionId),
                new KeyValuePair<string, string>("communicationSessionId", request.CommunicationSessionId),
                new KeyValuePair<string, string>("runControlRoot", communication.RunControlRoot),
                new KeyValuePair<string, string>("timeoutMs", ((int)request.Timeout.TotalMilliseconds).ToString())), cancellationToken).ConfigureAwait(false);

            var line = await transport.ReadCommandResponseLineAsync(CancellationToken.None, request.Timeout).ConfigureAwait(false);
            if (line is null)
            {
                return mapper.FailedEventBufferReset(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.EventBufferResetWorkerDisconnected",
                    "PLCSIM Advanced V6 worker disconnected during EventBuffer reset request.");
            }

            var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
            if (!message.IsProtocolVersionSupported)
            {
                return mapper.FailedEventBufferReset(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.EventBufferResetProtocolUnsupported",
                    "PLCSIM Advanced V6 worker returned an unsupported protocol response during EventBuffer reset request.");
            }

            if (string.Equals(message.Type, "EventBufferResetRequested", StringComparison.OrdinalIgnoreCase))
            {
                var accepted = mapper.ReadBool(message.Values, "accepted");
                var evidence = mapper.CreateEventBufferResetEvidence(message.Values);
                if (cancellationToken.IsCancellationRequested)
                {
                    return new PlcsimEventBufferResetResult(
                        PlcsimOperationStatus.Cancelled,
                        accepted,
                        new[]
                        {
                            mapper.CreateDrainedCancellationDiagnostic(
                                "PlcsimAdvanced.V6.EventBufferResetCancelled",
                                "PLCSIM Advanced V6 EventBuffer reset write completed after cancellation; the accepted flag reports whether the PLC write succeeded.",
                                "EventBufferReset",
                                "plcsim-advanced-v6-event-buffer-reset")
                        },
                        evidence);
                }

                return new PlcsimEventBufferResetResult(
                    mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                    accepted,
                    Array.Empty<PlcsimDiagnostic>(),
                    evidence);
            }

            if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return new PlcsimEventBufferResetResult(
                    mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                    Accepted: false,
                    new[]
                    {
                        mapper.CreateWorkerDiagnostic(
                            message.Values,
                            "PlcsimAdvanced.V6.EventBufferResetFailed",
                            "PLCSIM Advanced V6 worker reported an EventBuffer reset error.",
                            "EventBufferReset",
                            "plcsim-advanced-v6-event-buffer-reset")
                    },
                    Array.Empty<PlcsimEvidence>());
            }

            return mapper.FailedEventBufferReset(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.EventBufferResetUnexpectedResponse",
                "PLCSIM Advanced V6 worker returned an unexpected response during EventBuffer reset request.");
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return mapper.FailedEventBufferReset(
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.EventBufferResetCancelled",
                "PLCSIM Advanced V6 EventBuffer reset request was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            Dispose();
            return mapper.FailedEventBufferReset(
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.EventBufferResetTimedOut",
                "PLCSIM Advanced V6 worker did not complete EventBuffer reset request before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return mapper.FailedEventBufferReset(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.EventBufferResetIpcFailed",
                $"PLCSIM Advanced V6 EventBuffer reset IPC failed: {ex.Message}");
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }


    public Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
        PlcsimEventStreamMonitoringRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        return RequestEventStreamMonitoringAsync(
            "StartEventStreamMonitoring",
            "EventStreamMonitoringStarted",
            request.CommunicationSessionId,
            communication,
            requireRunningRuntime: true,
            cancellationToken);
    }

    public Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
        PlcsimEventStreamStopRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        return RequestEventStreamMonitoringAsync(
            "StopEventStreamMonitoring",
            "EventStreamMonitoringStopped",
            request.CommunicationSessionId,
            communication,
            requireRunningRuntime: false,
            cancellationToken);
    }

    private async Task<PlcsimEventStreamMonitoringResult> RequestEventStreamMonitoringAsync(
        string requestType,
        string responseType,
        string communicationSessionId,
        PlcsimCommunicationSessionInfo communication,
        bool requireRunningRuntime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return mapper.FailedEventStreamMonitoring(
                communicationSessionId,
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.WorkerUnavailable",
                "PLCSIM Advanced V6 worker is not available for EventStream monitoring.");
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            if (requireRunningRuntime)
            {
                var runtimeAvailability = await ObserveRuntimeAvailabilityUnderLockAsync(cancellationToken).ConfigureAwait(false);
                if (runtimeAvailability is not null)
                {
                    return mapper.FailedEventStreamMonitoring(
                        communicationSessionId,
                        runtimeAvailability.Status,
                        runtimeAvailability.Code,
                        runtimeAvailability.Message);
                }
            }

            await transport.SendRequestAsync(transport.CreateCommandRequest(
                requestType,
                new KeyValuePair<string, string>("runtimeSessionId", communication.RuntimeSessionId),
                new KeyValuePair<string, string>("communicationSessionId", communication.SessionId),
                new KeyValuePair<string, string>("runControlRoot", communication.RunControlRoot),
                new KeyValuePair<string, string>("eventBufferRoot", communication.EventBufferRoot)), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var line = await transport.ReadCommandResponseLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null)
                {
                    return mapper.FailedEventStreamMonitoring(
                        communicationSessionId,
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.EventStreamMonitoringWorkerDisconnected",
                        "PLCSIM Advanced V6 worker disconnected during EventStream monitoring.");
                }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
                if (!message.IsProtocolVersionSupported)
                {
                    return mapper.FailedEventStreamMonitoring(
                        communicationSessionId,
                        PlcsimOperationStatus.Failed,
                        "PlcsimAdvanced.V6.EventStreamMonitoringProtocolUnsupported",
                        "PLCSIM Advanced V6 worker returned an unsupported protocol response during EventStream monitoring.");
                }

                if (string.Equals(message.Type, responseType, StringComparison.OrdinalIgnoreCase))
                {
                    var state = mapper.CreateEventStreamMonitoringState(message.Values, communicationSessionId);
                    var evidence = mapper.CreateRuntimeEvidence(
                        "plcsim-advanced-v6-eventstream-monitoring",
                        "EventStreamMonitoring");
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new PlcsimEventStreamMonitoringResult(
                            PlcsimOperationStatus.Cancelled,
                            state,
                            new[]
                            {
                                mapper.CreateDrainedCancellationDiagnostic(
                                    "PlcsimAdvanced.V6.EventStreamMonitoringCancelled",
                                    "PLCSIM Advanced V6 EventStream monitoring state changed after cancellation; the worker remains available and the returned state reports the completed transition.",
                                    requestType,
                                    "plcsim-advanced-v6-eventstream-monitoring")
                            },
                            evidence);
                    }

                    return new PlcsimEventStreamMonitoringResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                        state,
                        Array.Empty<PlcsimDiagnostic>(),
                        evidence);
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new PlcsimEventStreamMonitoringResult(
                        mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                        message.Values.ContainsKey("lastReadState")
                            ? mapper.CreateEventStreamMonitoringState(message.Values, communicationSessionId)
                            : mapper.CreateUnavailableEventStreamMonitoringState(communicationSessionId),
                        new[]
                        {
                            mapper.CreateWorkerDiagnostic(
                                message.Values,
                                "PlcsimAdvanced.V6.EventStreamMonitoringFailed",
                                "PLCSIM Advanced V6 worker reported an EventStream monitoring error.",
                                requestType,
                                "plcsim-advanced-v6-eventstream-monitoring")
                        },
                        Array.Empty<PlcsimEvidence>());
                }

                return mapper.FailedEventStreamMonitoring(
                    communicationSessionId,
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.EventStreamMonitoringUnexpectedResponse",
                    "PLCSIM Advanced V6 worker returned an unexpected response during EventStream monitoring.");
            }
        }
        catch (OperationCanceledException)
        {
            PublishEventStreamMonitoringFault(
                communicationSessionId,
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.EventStreamMonitoringCancelled",
                "PLCSIM Advanced V6 EventStream monitoring was cancelled after the request entered the IPC boundary.",
                PlcsimDiagnosticSeverity.Warning);
            Terminate("PLCSIM Advanced V6 EventStream monitoring was cancelled after the request entered the IPC boundary.");
            return mapper.FailedEventStreamMonitoring(
                communicationSessionId,
                PlcsimOperationStatus.Cancelled,
                "PlcsimAdvanced.V6.EventStreamMonitoringCancelled",
                "PLCSIM Advanced V6 EventStream monitoring request was cancelled.",
                PlcsimDiagnosticSeverity.Warning);
        }
        catch (TimeoutException)
        {
            PublishEventStreamMonitoringFault(
                communicationSessionId,
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.EventStreamMonitoringTimedOut",
                "PLCSIM Advanced V6 EventStream monitoring timed out after the request entered the IPC boundary.");
            Terminate("PLCSIM Advanced V6 EventStream monitoring timed out after the request entered the IPC boundary.");
            return mapper.FailedEventStreamMonitoring(
                communicationSessionId,
                PlcsimOperationStatus.TimedOut,
                "PlcsimAdvanced.V6.EventStreamMonitoringTimedOut",
                "PLCSIM Advanced V6 worker did not complete EventStream monitoring before the command timeout.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            PublishEventStreamMonitoringFault(
                communicationSessionId,
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.EventStreamMonitoringIpcFailed",
                "PLCSIM Advanced V6 EventStream monitoring failed at the IPC boundary.");
            Terminate("PLCSIM Advanced V6 EventStream monitoring failed at the IPC boundary.");
            return new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Failed,
                mapper.CreateUnavailableEventStreamMonitoringState(communicationSessionId),
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.EventStreamMonitoringIpcFailed",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker IPC failed during EventStream monitoring.",
                        requestType,
                        "plcsim-advanced-v6-eventstream-monitoring")
                },
                new[]
                {
                    new PlcsimEvidence(
                        "plcsim-advanced-v6-eventstream-monitoring-ipc-error",
                        "WorkerIpcError",
                        ex.Message)
                });
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    public async Task<PlcsimCommunicationCloseResult> CloseCommunicationAsync(
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!transport.IsAvailable)
        {
            return new PlcsimCommunicationCloseResult(
                PlcsimOperationStatus.SucceededWithWarnings,
                communication.SessionId,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.CommunicationCloseWorkerUnavailable",
                        PlcsimDiagnosticSeverity.Warning,
                        "PLCSIM Advanced V6 worker was already unavailable while closing UnitTest communication.",
                        "CloseCommunication",
                        "plcsim-advanced-v6-communication-close")
                },
                Array.Empty<PlcsimEvidence>());
        }

        await transport.AcquireCommandAsync(cancellationToken).ConfigureAwait(false);
        transport.ThrowIfCancellationRequestedBeforeDispatch(cancellationToken);
        try
        {
            await transport.SendRequestAsync(transport.CreateCommandRequest(
                "CloseCommunication",
                new KeyValuePair<string, string>("runtimeSessionId", communication.RuntimeSessionId),
                new KeyValuePair<string, string>("communicationSessionId", communication.SessionId)), cancellationToken).ConfigureAwait(false);

            var line = await transport.ReadCommandResponseLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return new PlcsimCommunicationCloseResult(
                    PlcsimOperationStatus.SucceededWithWarnings,
                    communication.SessionId,
                    new[]
                    {
                        new PlcsimDiagnostic(
                            "PlcsimAdvanced.V6.CommunicationCloseWorkerDisconnected",
                            PlcsimDiagnosticSeverity.Warning,
                            "PLCSIM Advanced V6 worker disconnected while closing UnitTest communication.",
                            "CloseCommunication",
                            "plcsim-advanced-v6-communication-close")
                    },
                    Array.Empty<PlcsimEvidence>());
            }

                var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
            if (string.Equals(message.Type, "CommunicationClosed", StringComparison.OrdinalIgnoreCase))
            {
                var sessionId = mapper.ReadString(message.Values, "communicationSessionId", communication.SessionId);
                return new PlcsimCommunicationCloseResult(
                    mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded),
                    sessionId,
                    Array.Empty<PlcsimDiagnostic>(),
                    new[]
                    {
                        new PlcsimEvidence(
                            "plcsim-advanced-v6-communication-close",
                            "CloseCommunication",
                            sessionId)
                    });
            }

            if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return new PlcsimCommunicationCloseResult(
                    mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                    communication.SessionId,
                    new[]
                    {
                        mapper.CreateWorkerDiagnostic(
                            message.Values,
                            "PlcsimAdvanced.V6.CommunicationCloseFailed",
                            "PLCSIM Advanced V6 worker reported a UnitTest communication close error.",
                            "CloseCommunication",
                            "plcsim-advanced-v6-communication-close")
                    },
                    Array.Empty<PlcsimEvidence>());
            }

            return new PlcsimCommunicationCloseResult(
                PlcsimOperationStatus.Failed,
                communication.SessionId,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.CommunicationCloseUnexpectedResponse",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker returned an unexpected response during UnitTest communication close.",
                        "CloseCommunication",
                        "plcsim-advanced-v6-communication-close")
                },
                Array.Empty<PlcsimEvidence>());
        }
        catch (OperationCanceledException)
        {
            Dispose();
            return new PlcsimCommunicationCloseResult(
                PlcsimOperationStatus.Cancelled,
                communication.SessionId,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.CommunicationCloseCancelled",
                        PlcsimDiagnosticSeverity.Warning,
                        "PLCSIM Advanced V6 UnitTest communication close request was cancelled.",
                        "CloseCommunication",
                        "plcsim-advanced-v6-communication-close")
                },
                Array.Empty<PlcsimEvidence>());
        }
        catch (TimeoutException)
        {
            Dispose();
            return new PlcsimCommunicationCloseResult(
                PlcsimOperationStatus.TimedOut,
                communication.SessionId,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.CommunicationCloseTimedOut",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V6 worker did not answer the UnitTest communication close request before the timeout.",
                        "CloseCommunication",
                        "plcsim-advanced-v6-communication-close")
                },
                Array.Empty<PlcsimEvidence>());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            return new PlcsimCommunicationCloseResult(
                PlcsimOperationStatus.SucceededWithWarnings,
                communication.SessionId,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V6.CommunicationCloseIpcInterrupted",
                        PlcsimDiagnosticSeverity.Warning,
                        $"PLCSIM Advanced V6 worker IPC was interrupted during UnitTest communication close: {ex.Message}",
                        "CloseCommunication",
                        "plcsim-advanced-v6-communication-close")
                },
                Array.Empty<PlcsimEvidence>());
        }
        finally
        {
            transport.ReleaseCommand();
        }
    }

    private void HandleConnectionLost(string message)
    {
        eventRouter.PublishConnectionLost(mapper.CreateConnectionLostChange(message));
        eventRouter.ClearAsyncEventHandlers();
    }

    private bool TryHandleAsyncMessage(string line)
    {
        var workerEvent = mapper.MapAsyncMessage(line);
        return workerEvent is not null && eventRouter.TryRoute(workerEvent);
    }

    private void PublishEventStreamMonitoringFault(
        string communicationSessionId,
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        eventRouter.TryRoute(
            mapper.CreateEventStreamMonitoringFault(
                communicationSessionId,
                status,
                code,
                message,
                severity));
    }

    private async Task<RuntimeAvailabilityFailure?> ObserveRuntimeAvailabilityUnderLockAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await transport.SendRequestAsync(transport.CreateCommandRequest("ObserveRuntime"), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var line = await transport.ReadCommandResponseLineAsync(CancellationToken.None).ConfigureAwait(false);
            if (line is null)
            {
                return new RuntimeAvailabilityFailure(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RunControlRuntimeObservationWorkerDisconnected",
                    "PLCSIM Advanced V6 worker disconnected while checking runtime state before RunControl communication.");
            }

            var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
            if (!message.IsProtocolVersionSupported)
            {
                return new RuntimeAvailabilityFailure(
                    PlcsimOperationStatus.Failed,
                    "PlcsimAdvanced.V6.RunControlRuntimeObservationProtocolUnsupported",
                    "PLCSIM Advanced V6 worker returned an unsupported protocol response while checking runtime state before RunControl communication.");
            }

            if (string.Equals(message.Type, "RuntimeObserved", StringComparison.OrdinalIgnoreCase))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new RuntimeAvailabilityFailure(
                        PlcsimOperationStatus.Cancelled,
                        "PlcsimAdvanced.V6.RuntimeAvailabilityObservationCancelled",
                        "PLCSIM Advanced V6 runtime availability observation was cancelled after the worker response was drained.");
                }

                var status = mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Succeeded);
                var runtime = mapper.CreateRuntimeTarget(message.Values);
                if (status is not (PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings))
                {
                    return new RuntimeAvailabilityFailure(
                        status,
                        "PlcsimAdvanced.V6.RunControlRuntimeObservationFailed",
                        $"PLCSIM Advanced V6 runtime observation returned {status} before RunControl communication.");
                }

                if (runtime.State != PlcsimRuntimeControlState.Run)
                {
                    return new RuntimeAvailabilityFailure(
                        PlcsimOperationStatus.Unavailable,
                        "PlcsimAdvanced.V6.RunControlRuntimeNotRunning",
                        $"PLCSIM Advanced V6 runtime is {runtime.State}; RunControl communication requires a running PLC runtime.");
                }

                return null;
            }

            if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
            {
                var status = mapper.ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed);
                return new RuntimeAvailabilityFailure(
                    status,
                    mapper.ReadString(message.Values, "code", "PlcsimAdvanced.V6.RunControlRuntimeObservationFailed"),
                    mapper.ReadString(message.Values, "message", "PLCSIM Advanced V6 worker reported a runtime observation error before RunControl communication."));
            }

            return new RuntimeAvailabilityFailure(
                PlcsimOperationStatus.Failed,
                "PlcsimAdvanced.V6.RunControlRuntimeObservationUnexpectedResponse",
                "PLCSIM Advanced V6 worker returned an unexpected response while checking runtime state before RunControl communication.");
        }
    }

    public void Terminate(string message)
    {
        transport.Terminate(message);
        eventRouter.ClearAsyncEventHandlers();
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            eventRouter.ClearAsyncEventHandlers();
        }
    }

    private void Dispose()
    {
        transport.Dispose();
        eventRouter.ClearAsyncEventHandlers();
    }

}

internal sealed record RuntimeAvailabilityFailure(
    PlcsimOperationStatus Status,
    string Code,
    string Message);

internal sealed record PlcsimAdvancedV6PipeConnectResult(
    bool Succeeded,
    string Code,
    string Message,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public static PlcsimAdvancedV6PipeConnectResult SucceededResult()
    {
        return new PlcsimAdvancedV6PipeConnectResult(true, string.Empty, string.Empty, Array.Empty<PlcsimEvidence>());
    }

    public static PlcsimAdvancedV6PipeConnectResult Failed(
        string code,
        string message,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        return new PlcsimAdvancedV6PipeConnectResult(false, code, message, evidence);
    }
}

internal sealed record PlcsimAdvancedV6WorkerResponse(
    bool IsReady,
    string? SessionId,
    string? Code,
    string? Message)
{
    public static PlcsimAdvancedV6WorkerResponse Parse(string line)
    {
        var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
        if (!message.IsProtocolVersionSupported)
        {
            return Error("PlcsimAdvanced.V6.WorkerInvalidResponse", "Worker response protocol version was not recognized.");
        }

        return message.Type switch
        {
            "Ready" => new PlcsimAdvancedV6WorkerResponse(
                true,
                message.Values.TryGetValue("sessionId", out var sessionId) ? sessionId : null,
                null,
                null),
            "Error" => new PlcsimAdvancedV6WorkerResponse(
                false,
                null,
                message.Values.TryGetValue("code", out var code) ? code : "PlcsimAdvanced.V6.WorkerOpenFailed",
                message.Values.TryGetValue("message", out var errorMessage) ? errorMessage : "Worker reported an error."),
            _ => Error("PlcsimAdvanced.V6.WorkerInvalidResponse", "Worker response was not recognized.")
        };
    }

    private static PlcsimAdvancedV6WorkerResponse Error(string code, string message)
    {
        return new PlcsimAdvancedV6WorkerResponse(false, null, code, message);
    }
}

internal static class PlcsimAdvancedV6WorkerProtocol
{
    public const string Version = "SIEMENS-WORKER/1";

    public static string CreateRequest(string type)
    {
        return CreateRequest(type, Array.Empty<KeyValuePair<string, string>>());
    }

    public static string CreateRequest(
        string type,
        params KeyValuePair<string, string>[] values)
    {
        var message = Version + "|type=" + Uri.EscapeDataString(type);
        foreach (var value in values)
        {
            message += "|" + value.Key + "=" + Uri.EscapeDataString(value.Value ?? string.Empty);
        }

        return message;
    }

    public static PlcsimAdvancedV6WorkerProtocolMessage ParseMessage(string line)
    {
        var parts = (line ?? string.Empty).Split('|');
        if (parts.Length == 0 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            return new PlcsimAdvancedV6WorkerProtocolMessage(false, string.Empty, new Dictionary<string, string>());
        }

        var values = parts
            .Skip(1)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.OrdinalIgnoreCase);

        return new PlcsimAdvancedV6WorkerProtocolMessage(
            true,
            values.TryGetValue("type", out var type) ? type : string.Empty,
            values);
    }
}

internal sealed record PlcsimAdvancedV6WorkerProtocolMessage(
    bool IsProtocolVersionSupported,
    string Type,
    IReadOnlyDictionary<string, string> Values);
